using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;

namespace EntityFramework.Utilities
{
	public interface IQueryProvider
	{
		bool CanDelete { get; }
		bool CanUpdate { get; }
		bool CanInsert { get; }
		bool CanBulkUpdate { get; }

		string GetDeleteQuery(QueryInformation queryInformation);
		string GetUpdateQuery(QueryInformation predicateQueryInfo, QueryInformation modificationQueryInfo);
		
		// made the SQLBulkCopyOptions as object to support SQLite bulk functionality but SQLBulkCopyOptions is not used by SQLite provider
		// in short, copyOptions is made into a generic type to support SQLite EF bulk operations
		Task InsertItems<T>(IEnumerable<T> items, string schema, string tableName, IList<ColumnMapping> properties, DbConnection storeConnection, int? batchSize, int? executeTimeout, DbTransaction transaction, object copyOptions);
		Task UpdateItems<T>(IEnumerable<T> items, string schema, string tableName, IList<ColumnMapping> properties, DbConnection storeConnection, int? batchSize, UpdateSpecification<T> updateSpecification, int? executeTimeout, DbTransaction transaction, DbConnection insertConnection, object copyOptions);

		bool CanHandle(DbConnection storeConnection);

		QueryInformation GetQueryInformation<T>(System.Data.Entity.Core.Objects.ObjectQuery<T> query);
	}
}
