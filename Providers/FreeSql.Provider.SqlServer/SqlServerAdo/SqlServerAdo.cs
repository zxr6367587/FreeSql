﻿using FreeSql.Internal;
using FreeSql.Internal.Model;
using FreeSql.Internal.ObjectPool;
using System;
using System.Collections;
using System.Data.Common;
#if microsoft
using Microsoft.Data.SqlClient;
#else
using System.Data.SqlClient;
#endif
using System.Linq;
using System.Threading;
using FreeSql.Internal.CommonProvider;

namespace FreeSql.SqlServer
{
    class SqlServerAdo : FreeSql.Internal.CommonProvider.AdoProvider
    {
        public SqlServerAdo() : base(DataType.SqlServer, null, null) { }
        public SqlServerAdo(CommonUtils util, string masterConnectionString, string[] slaveConnectionStrings, Func<DbConnection> connectionFactory) : base(DataType.SqlServer, masterConnectionString, slaveConnectionStrings)
        {
            base._util = util;
            if (connectionFactory != null)
            {
                var pool = new FreeSql.Internal.CommonProvider.DbConnectionPool(DataType.SqlServer, connectionFactory);
                ConnectionString = pool.TestConnection?.ConnectionString;
                MasterPool = pool;
                _CreateCommandConnection = pool.TestConnection;
                return;
            }

            var isAdoPool = masterConnectionString?.StartsWith("AdoConnectionPool,") ?? false;
            if (isAdoPool) masterConnectionString = masterConnectionString.Substring("AdoConnectionPool,".Length);
            if (!string.IsNullOrEmpty(masterConnectionString))
                MasterPool = isAdoPool ? 
                    new DbConnectionStringPool(base.DataType, CoreErrorStrings.S_MasterDatabase, () => new SqlConnection(masterConnectionString)) as IObjectPool<DbConnection> :  
                    new SqlServerConnectionPool(CoreErrorStrings.S_MasterDatabase, masterConnectionString, null, null);

            slaveConnectionStrings?.ToList().ForEach(slaveConnectionString =>
            {
                var slavePool = isAdoPool ?
                    new DbConnectionStringPool(base.DataType, $"{CoreErrorStrings.S_SlaveDatabase}{SlavePools.Count + 1}", () => new SqlConnection(slaveConnectionString)) as IObjectPool<DbConnection> :
                    new SqlServerConnectionPool($"{CoreErrorStrings.S_SlaveDatabase}{SlavePools.Count + 1}", slaveConnectionString, () => Interlocked.Decrement(ref slaveUnavailables), () => Interlocked.Increment(ref slaveUnavailables));
                SlavePools.Add(slavePool);
            });
        }
        
        static DateTime dt1970 = new DateTime(1970, 1, 1);
        static string[] ncharDbTypes = new[] { "NVARCHAR", "NCHAR", "NTEXT" };
        public override object AddslashesProcessParam(object param, Type mapType, ColumnInfo mapColumn)
        {
            if (param == null) return "NULL";
            if (mapType != null && mapType != param.GetType() && (param is IEnumerable == false))
                param = Utils.GetDataReaderValue(mapType, param);

            if (param is bool || param is bool?)
                return (bool)param ? 1 : 0;
            else if (param is string)
            {
                if (mapColumn != null && mapColumn.CsType.NullableTypeOrThis() == typeof(string) && !string.IsNullOrWhiteSpace(mapColumn.Attribute.DbType) && ncharDbTypes.Any(a => mapColumn.Attribute.DbType.Contains(a)) == false)
                    return string.Concat("'", param.ToString().Replace("'", "''"), "'");
                return string.Concat("N'", param.ToString().Replace("'", "''"), "'");
            }
            else if (param is char)
                return string.Concat("'", param.ToString().Replace("'", "''").Replace('\0', ' '), "'");
            else if (param is Enum)
                return AddslashesTypeHandler(param.GetType(), param) ?? ((Enum)param).ToInt64();
            else if (decimal.TryParse(string.Concat(param), out var trydec))
                return param;

            else if (param is DateTime)
            {
                var result = AddslashesTypeHandler(typeof(DateTime), param);
                if (result != null) return result;
                if (param.Equals(DateTime.MinValue) == true) param = new DateTime(1970, 1, 1);
                return string.Concat("'", ((DateTime)param).ToString("yyyy-MM-dd HH:mm:ss.fff"), "'");
            }
            else if (param is DateTime?)
            {
                var result = AddslashesTypeHandler(typeof(DateTime?), param);
                if (result != null) return result;
                if (param.Equals(DateTime.MinValue) == true) param = new DateTime(1970, 1, 1);
                return string.Concat("'", ((DateTime)param).ToString("yyyy-MM-dd HH:mm:ss.fff"), "'");
            }

            else if (param is DateTimeOffset || param is DateTimeOffset?)
            {
                if (param.Equals(DateTimeOffset.MinValue) == true) param = new DateTimeOffset(new DateTime(1970, 1, 1), TimeSpan.Zero);
                return string.Concat("'", ((DateTimeOffset)param).ToString("yyyy-MM-dd HH:mm:ss.fff zzzz"), "'");
            }
#if net60
            else if (param is DateOnly)
            {
                var result = AddslashesTypeHandler(typeof(DateOnly), param);
                if (result != null) return result;
                if (param.Equals(DateOnly.MinValue) == true) param = new DateOnly(1970, 1, 1);
                return string.Concat("'", ((DateOnly)param).ToString("yyyy-MM-dd"), "'");
            }
            else if (param is DateOnly?)
            {
                var result = AddslashesTypeHandler(typeof(DateOnly?), param);
                if (result != null) return result;
                if (param.Equals(DateOnly.MinValue) == true) param = new DateOnly(1970, 1, 1);
                return string.Concat("'", ((DateOnly)param).ToString("yyyy-MM-dd"), "'");
            }
            else if (param is TimeOnly || param is TimeOnly?)
            {
                var ts = (TimeOnly)param;
                return $"'{ts.Hour}:{ts.Minute}:{ts.Second}'";
            }
#endif
            else if (param is TimeSpan || param is TimeSpan?)
            {
                var ts = (TimeSpan)param;
                return $"'{ts.Hours}:{ts.Minutes}:{ts.Seconds}.{ts.Milliseconds}'";
            }
            else if (param is byte[])
                return $"0x{CommonUtils.BytesSqlRaw(param as byte[])}";
            else if (param is IEnumerable)
                return AddslashesIEnumerable(param, mapType, mapColumn);

            return string.Concat("'", param.ToString().Replace("'", "''"), "'");
        }

        DbConnection _CreateCommandConnection;
        public override DbCommand CreateCommand()
        {
            if (_CreateCommandConnection != null)
            {
                var cmd = _CreateCommandConnection.CreateCommand();
                cmd.Connection = null;
                return cmd;
            }
            return new SqlCommand();
        }

        public override void ReturnConnection(IObjectPool<DbConnection> pool, Object<DbConnection> conn, Exception ex)
        {
            var rawPool = pool as SqlServerConnectionPool;
            if (rawPool != null) rawPool.Return(conn, ex);
            else pool.Return(conn);
        }

        public override DbParameter[] GetDbParamtersByObject(string sql, object obj) => _util.GetDbParamtersByObject(sql, obj);
    }
}