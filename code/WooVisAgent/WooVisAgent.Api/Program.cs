using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using System.Reflection;
using System.Security.Claims;
using WooVisAgent.Api.Extensions;
using WooVisAgent.Api.Hubs;
using WooVisAgent.Api.Services;
using WooVisAgent.EntityFrameworkCore;
using WooVisAgent.EventBus;
using WooVisAgent.Infrastructure.Storage;
using WooVisAgent.Services.Common.Behaviors;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.Services.Common.Storage;
using WooVisAgentServices.IdentityService;
using WooVisAgentServices.IdentityService.Options;
using WooVisAgentServices.ImageService;
using WooVisAgentServices.VideoService;
using WooVisAgentServices.AgentService;
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

        // 视频生成用例：与图片同构的异步链路（提交 → RabbitMQ → Worker 生成 → SignalR 推送）。
        // 放在 AddImageService 之后，因为提交用例复用了它的通用派发器
        builder.AddVideoService();

        // Agent 能力（Microsoft Agent Framework）：只做提示词润色与参数建议，
        // 不提交任务、不参与轮询。注册期不发任何外部请求，没配语言模型也不影响启动
        builder.AddAgentService();

        // 产物落盘实现。与 GenerateWorker 使用同一实现、同一目录，
        // 这样 Worker 写出的图片能被下面挂载的 /uploads 静态托管读到
        builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();

        // 任务实时通道（/hubs/tasks），把生成进度与产物推给前端页面
        builder.Services.AddSignalR();

        // RabbitMQ 事件总线（Wolverine）：只扫描 Api 自身程序集。
        // 本程序集里只有一个处理器——GenerationTaskStatusChangedConsumer（状态 -> SignalR），
        // 生成任务消息的处理器在 GenerateWorker 里，因此 Api 不会重复消费生成任务。
        builder.AddEventBus(EventBusConstants.ApiQueue, typeof(Program).Assembly);
        builder.Services.AddMediatR(cfg =>
        {
            cfg.LicenseKey =
                "eyJhbGciOiJSUzI1NiIsImtpZCI6Ikx1Y2t5UGVubnlTb2Z0d2FyZUxpY2Vuc2VLZXkvYmJiMTNhY2I1OTkwNGQ4OWI0Y2IxYzg1ZjA4OGNjZjkiLCJ0eXAiOiJKV1QifQ.eyJpc3MiOiJodHRwczovL2x1Y2t5cGVubnlzb2Z0d2FyZS5jb20iLCJhdWQiOiJMdWNreVBlbm55U29mdHdhcmUiLCJleHAiOiIxNzg0NzY0ODAwIiwiaWF0IjoiMTc1MzIzNjIyMSIsImFjY291bnRfaWQiOiIwMTk4MzUwNDdkNGI3ZmU5YmZlMzdhMWQ2MDQwMzM4NSIsImN1c3RvbWVyX2lkIjoiY3RtXzAxazB0Z2JiNHZ2N2tnZDlyMXBtc2pmNG4xIiwic3ViX2lkIjoiLSIsImVkaXRpb24iOiIwIiwidHlwZSI6IjIifQ.s5kG1QZdtbY_jtqsxQpdOQUSoXFb5MwFGp6AP1rBqPycBn03RUsmFVHaAQJKBMOHvTUGLLJPts1q7TaY7pV2Dut5n0LtnNXaq4r8AZ5rOSQWOAcLfuMUFMLDwhR9BGuPODvNje74evts-4zB6qJKwxcdk8a-DrN1qGEQcB3Zksh1Su02jIDBiUjvAG07wjUdt-n8AdMF2kM-hPAMdxBV4Wr_cqJV_EbimBAiEeMUpey7G4qaLPcsJo0lKu7T6KRjc3YNpiZ9hGh9Tf_JWHMS__ed9wpueK6kvFjwQuBAGjpFb51FFdQDUh2Uuuuo7ldvofBSaX6xIfXjLL1hqQ0MEQ";

            cfg.RegisterServicesFromAssembly(Assembly.GetAssembly(typeof(WooVisAgentServices.IdentityService.DependencyInjection))!);
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehavior<,>));

            // 给每个用例（Command / Query）自动套 span。
            // 挂在管道里而不是逐个手写：新增用例自动获得链路，登录也不例外；
            // 逐个改既容易漏，新增接口又会退化成「这个接口没有追踪」。
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(TracingBehavior<,>));
        });

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
