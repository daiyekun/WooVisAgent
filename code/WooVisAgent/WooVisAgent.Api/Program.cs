using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using WooVisAgent.Api.Extensions;
using WooVisAgent.Api.Services;
using WooVisAgent.EntityFrameworkCore;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgentServices.IdentityService;
using WooVisAgentServices.IdentityService.Options;
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
