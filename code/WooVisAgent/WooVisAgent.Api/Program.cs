using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using WooVisAgent.Api.Extensions;
using WooVisAgent.Api.Hubs;
using WooVisAgent.Api.Services;
using WooVisAgent.EntityFrameworkCore;
using WooVisAgent.EventBus;
using WooVisAgent.Infrastructure.Storage;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.Services.Common.Storage;
using WooVisAgentServices.IdentityService;
using WooVisAgentServices.IdentityService.Options;
using WooVisAgentServices.ImageService;
using WooVisAgentServices.ModelService;

namespace WooVisAgent.Api;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // 数据库 + Identity 存储（AddEfCore），身份用例与 JWT 签发（AddIdentityService）
        builder.AddEfCore();
        builder.AddIdentityService();
        // 模型配置用例：64 位雪花主键、连通性测试、供应方模型清单
        builder.AddModelService();

        // 图片生成用例：提交任务后把消息投递到 RabbitMQ；
        // 真正的生成由独立服务 WooVisAgent.GenerateWorker 消费队列后执行
        builder.AddImageService();

        // 产物落盘实现。与 GenerateWorker 使用同一实现、同一目录，
        // 这样 Worker 写出的图片能被下面挂载的 /uploads 静态托管读到
        builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();

        // 任务实时通道（/hubs/tasks），把生成进度与产物推给前端页面
        builder.Services.AddSignalR();

        // RabbitMQ 事件总线：只扫描 Api 自身程序集。
        // 本程序集里只有一个消费者——GenerationTaskStatusChangedConsumer（状态 -> SignalR），
        // 生成任务消息的消费者在 GenerateWorker 里，因此 Api 不会重复消费生成任务。
        builder.AddEventBus(typeof(Program).Assembly);

        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                // 64 位主键超出 JS 安全整数范围，统一以字符串输出给前端（详见 LongToStringJsonConverter）
                options.JsonSerializerOptions.Converters.Add(new LongToStringJsonConverter());
                options.JsonSerializerOptions.Converters.Add(new NullableLongToStringJsonConverter());
            });
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        builder.Services.AddAuthorization();

        AddJwtAuthentication(builder);

        // 前端默认走 vite 同源代理，这里额外放开开发环境直连（不带 Cookie，无需凭据）
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddCors(options => options.AddPolicy("webui", policy => policy
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod()));
        }

        var app = builder.Build();

        app.MapDefaultEndpoints();

        // 生成产物通过 /uploads 静态托管给页面。
        // 目录与 GenerateWorker 落盘目录共用同一组常量，避免"写这边、读那边"。
        var uploadsRoot = Path.Combine(LocalStorageDefaults.RootPath, LocalStorageDefaults.UploadFolder);
        Directory.CreateDirectory(uploadsRoot);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(uploadsRoot),
            RequestPath = LocalStorageDefaults.PublicRequestPath,
        });

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseCors("webui");
        }

        if (!bool.TryParse(builder.Configuration["Database:AutoInitialize"], out var autoInitialize) || autoInitialize)
        {
            // 首次启动建表 + 内置角色种子（当前仓库暂无 EF 迁移，用 EnsureCreated 保证开箱可用）
            await app.Services.InitializeDatabaseAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();

        // 任务实时通道：前端 useSignalR.ts 里的 TASK_HUB_PATH
        app.MapHub<TaskHub>("/hubs/tasks");

        await app.RunAsync();
    }

    /// <summary>
    /// JwtBearer 认证：与 WooVisAgentServices.IdentityService 的 JwtTokenGenerator 使用同一组密钥与声明类型。
    /// </summary>
    private static void AddJwtAuthentication(WebApplicationBuilder builder)
    {
        var jwtOptions = JwtOptions.FromConfiguration(builder.Configuration);

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(jwtOptions.GetSigningKeyBytes()),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = ClaimTypes.Name,
                    RoleClaimType = ClaimTypes.Role,
                };

                // SignalR 的 WebSocket 握手无法携带请求头，约定通过 access_token 查询参数传递
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken)
                            && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                };
            });
    }
}
