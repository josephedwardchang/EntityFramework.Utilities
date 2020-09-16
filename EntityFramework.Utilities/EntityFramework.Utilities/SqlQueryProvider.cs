using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EntityFramework.Utilities
{
	public class SqlQueryProvider : IQueryProvider
	{
		public bool CanDelete => true;
		public bool CanUpdate => true;
		public bool CanInsert => true;
		public bool CanBulkUpdate => true;

		private static readonly Regex FromRegex = new Regex(@"FROM \[([^\]]+)\]\.\[([^\]]+)\] AS (\[[^\]]+\])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private static readonly Regex UpdateRegex = new Regex(@"(\[[^\]]+\])[^=]+=(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		public string GetDeleteQuery(QueryInformation queryInfo)
		{
			return $"DELETE {queryInfo.TopExpression} FROM [{queryInfo.Schema}].[{queryInfo.Table}] {queryInfo.WhereSql}";
		}

		public string GetUpdateQuery(QueryInformation predicateQueryInfo, QueryInformation modificationQueryInfo)
		{
			var msql = modificationQueryInfo.WhereSql.Replace("WHERE ", "");
			var indexOfAnd = msql.IndexOf("AND", StringComparison.Ordinal);
			var update = indexOfAnd == -1 ? msql : msql.Substring(0, indexOfAnd).Trim();

			var match = UpdateRegex.Match(update);
			string updateSql;

			if (match.Success)
			{
				var col = match.Groups[1];
				var rest = match.Groups[2].Value;

				rest = SqlStringHelper.FixParantheses(rest);

				updateSql = col.Value + " = " + rest;
			}
			else
			{
				updateSql = string.Join(" = ", update.Split(new[] { " = " }, StringSplitOptions.RemoveEmptyEntries).Reverse());
			}

			return
				$"UPDATE [{predicateQueryInfo.Schema}].[{predicateQueryInfo.Table}] SET {updateSql} {predicateQueryInfo.WhereSql}";
		}

		public async Task InsertItems<T>(IEnumerable<T> items, string schema, string tableName, IList<ColumnMapping> properties, DbConnection storeConnection, int? batchSize, int? executeTimeout, DbTransaction transaction, object copyOptions)
		{
			using (var reader = new EFDataReader<T>(items, properties))
			{
				if (storeConnection.State != System.Data.ConnectionState.Open)
				{
					storeConnection.Open();
				}

				using (var copy = new SqlBulkCopy((SqlConnection)storeConnection, (SqlBulkCopyOptions)copyOptions, transaction as SqlTransaction))
				{
					copy.BulkCopyTimeout = executeTimeout ?? 600;
					copy.BatchSize = batchSize ?? 15000; //default batch size

					if (!string.IsNullOrWhiteSpace(schema))
					{
						copy.DestinationTableName = $"[{schema}].[{tableName}]";
					}
					else
					{
						copy.DestinationTableName = "[" + tableName + "]";
					}

					copy.NotifyAfter = 0;

					foreach (var i in Enumerable.Range(0, reader.FieldCount))
					{
						copy.ColumnMappings.Add(i, properties[i].NameInDatabase);
					}

					await copy.WriteToServerAsync(reader);
					copy.Close();
				}
			}
		}

		public async Task UpdateItems<T>(IEnumerable<T> items, string schema, string tableName, IList<ColumnMapping> properties, DbConnection storeConnection, int? batchSize, UpdateSpecification<T> updateSpecification, int? executeTimeout, DbTransaction transaction, DbConnection insertConnection, object copyOptions)
		{
			var tempTableName = "#" + Guid.NewGuid().ToString("N");
			var columnsToUpdate = updateSpecification.Properties.Select(p => p.GetPropertyName()).ToDictionary(x => x);
			var filtered = properties.Where(p => columnsToUpdate.ContainsKey(p.NameOnObject) || p.IsPrimaryKey).ToList();
			var columns = filtered.Select(c => "[" + c.NameInDatabase + "] " + c.DataTypeFull + (c.DataType.EndsWith("char") ? " COLLATE DATABASE_DEFAULT" : null));
			var pkConstraint = string.Join(", ", properties.Where(p => p.IsPrimaryKey).Select(c => "[" + c.NameInDatabase + "]"));

			var str = $"CREATE TABLE [{schema}].[{tempTableName}]({string.Join(", ", columns)}, PRIMARY KEY ({pkConstraint}))";

			if (storeConnection.State != System.Data.ConnectionState.Open)
			{
				storeConnection.Open();
			}

			var setters = string.Join(",", filtered.Where(c => !c.IsPrimaryKey).Select(c => "ORIG.[" + c.NameInDatabase + "] = TEMP.[" + c.NameInDatabase + "]"));
			var pks = properties.Where(p => p.IsPrimaryKey).Select(x => "ORIG.[" + x.NameInDatabase + "] = TEMP.[" + x.NameInDatabase + "]");
			var filter = string.Join(" and ", pks);
			var mergeCommand = string.Format(@"UPDATE ORIG
				SET
					{4}
				FROM
					[{0}].[{1}] ORIG
				INNER JOIN
					 [{0}].[{2}] TEMP
				ON
					{3}", schema, tableName, tempTableName, filter, setters);

			using (var createCommand = storeConnection.CreateCommand())
			using (var mCommand = storeConnection.CreateCommand())
			using (var dCommand = storeConnection.CreateCommand())
			{
				createCommand.CommandText = str;
				mCommand.CommandText = mergeCommand;
				dCommand.CommandText = $"DROP table [{schema}].[{tempTableName}]";

				createCommand.CommandTimeout = executeTimeout ?? 600;
				mCommand.CommandTimeout = executeTimeout ?? 600;
				dCommand.CommandTimeout = executeTimeout ?? 600;

				createCommand.Transaction = transaction;
				mCommand.Transaction = transaction;
				dCommand.Transaction = transaction;

				createCommand.ExecuteNonQuery();
				await InsertItems(items, schema, tempTableName, filtered, insertConnection, batchSize, executeTimeout, transaction, (SqlBulkCopyOptions)copyOptions);
				mCommand.ExecuteNonQuery();
				dCommand.ExecuteNonQuery();
			}
		}

		public bool CanHandle(DbConnection storeConnection)
		{
			return storeConnection is SqlConnection;
		}

		public QueryInformation GetQueryInformation<T>(System.Data.Entity.Core.Objects.ObjectQuery<T> query)
		{
			var queryInfo = new QueryInformation();

			var str = query.ToTraceString();
			var match = FromRegex.Match(str);
			queryInfo.Schema = match.Groups[1].Value;
			queryInfo.Table = match.Groups[2].Value;
			queryInfo.Alias = match.Groups[3].Value;

			var i = str.IndexOf("WHERE", StringComparison.Ordinal);

			if (i > 0)
			{
				var whereClause = str.Substring(i);
				queryInfo.WhereSql = whereClause.Replace(queryInfo.Alias + ".", "");
			}

			return queryInfo;
		}
	}
}
