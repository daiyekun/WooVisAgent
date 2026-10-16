//using System.Collections.Generic;
//using System.Data;
//using System.Threading;
//using System.Threading.Tasks;

//namespace Zilor.AICopilot.Services.Common.Contracts;

//public interface IDatabaseConnector
//{
//    /// <summary>
//    /// 获取数据库连接（不打开，仅创建对象）
//    /// </summary>
//    IDbConnection GetConnection(BusinessDatabase database);

//    /// <summary>
//    /// 执行查询并返回动态列表
//    /// </summary>
//    /// <param name="database">目标数据库配置</param>
//    /// <param name="sql">SQL语句</param>
//    /// <param name="parameters">参数</param>
//    /// <param name="cancellationToken">取消令牌</param>
//    /// <returns>动态对象列表(IEnumerable of dynamic)</returns>
//    Task<IEnumerable<dynamic>> ExecuteQueryAsync(
//        BusinessDatabase database, 
//        string sql, 
//        object? parameters = null, 
//        CancellationToken cancellationToken = default);
//}