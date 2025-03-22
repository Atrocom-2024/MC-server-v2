using DotNetEnv;
using MC_server.API.Extensions;
using MC_server.API.Middleware;
using MC_server.Core.Extensions;

namespace MC_server.API
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // .env ���� �ε�
            Env.Load();

            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            // Core �� API ���� ���
            // .NET Core�� ������ ����(Dependency Injection, DI)
            // ��� ���񽺴� DI �����̳ʿ� ��ϵǾ�� ��Ÿ�ӿ��� ��� ����
            builder.Services.AddCoreServices();
            builder.Services.AddApiServices();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            // ���� ó�� �̵���� �߰� (���� ���� ����)
            app.UseMiddleware<GlobalExceptionMiddleware>();

            app.UseHttpsRedirection();

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
