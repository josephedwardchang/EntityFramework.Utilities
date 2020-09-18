using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Core.EntityClient;
using System.Data.Entity.Core.Objects;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace EntityFramework.Utilities
{
	public interface IEFBatchOperationBase<T> where T : class
	{
		/// <summary>
		/// Bulk insert all items if the Provider supports it. Otherwise it will use the default insert unless Configuration.DisableDefaultFallback is set to true in which case it would throw an exception.
		/// </summary>
		/// <param name="items">The items to insert</param>
		/// <param name="connection">The DbConnection to use for the insert. Only needed when for example a profiler wraps the connection. Then you need to provide a connection of the type the provider use.</param>
		/// <param name="batchSize">The size of each batch. Default depends on the provider. SqlProvider uses 15000 as default</param>
		Task InsertAllAsync<TEntity>(IEnumerable<TEntity> items, DbConnection connection = null, int? batchSize = null, int? executeTimeout = null, SqlBulkCopyOptions copyOptions = SqlBulkCopyOptions.Default, DbTransaction transaction = null) where TEntity : class, T;
		void InsertAll<TEntity>(IEnumerable<TEntity> items, DbConnection connection = null, int? batchSize = null, int? executeTimeout = null, SqlBulkCopyOptions copyOptions = SqlBulkCopyOptions.Default, DbTransaction transaction = null) where TEntity : class, T;
		IEFBatchOperationFiltered<T> Where(Expression<Func<T, bool>> predicate);

		/// <summary>
		/// Bulk update all items if the Provider supports it. Otherwise it will use the default update unless Configuration.DisableDefaultFallback is set to true in which case it would throw an exception.
		/// </summary>
		/// <typeparam name="TEntity"></typeparam>
		/// <param name="items">The items to update</param>
		/// <param name="updateSpecification">Define which columns to update</param>
		/// <param name="connection">The DbConnection to use for the insert. Only needed when for example a profiler wraps the connection. Then you need to provide a connection of the type the provider use.</param>
		/// <param name="batchSize">The size of each batch. Default depends on the provider. SqlProvider uses 15000 as default</param>
		Task UpdateAllAsync<TEntity>(IEnumerable<TEntity> items, Action<UpdateSpecification<TEntity>> updateSpecification, DbConnection connection = null, int? batchSize = null, int? executeTimeout = null, SqlBulkCopyOptions copyOptions = SqlBulkCopyOptions.Default, DbTransaction transaction = null) where TEntity : class, T;
		void UpdateAll<TEntity>(IEnumerable<TEntity> items, Action<UpdateSpecification<TEntity>> updateSpecification, DbConnection connection = null, int? batchSize = null, int? executeTimeout = null, SqlBulkCopyOptions copyOptions = SqlBulkCopyOptions.Default, DbTransaction transaction = null) where TEntity : class, T;
	}

	public class UpdateSpecification<T>
	{
		/// <summary>
		/// Set each column you want to update, Columns that belong to the primary key cannot be updated.
		/// </summary>
		/// <param name="properties"></param>
		/// <returns></returns>
		public UpdateSpecification<T> ColumnsToUpdate(params Expression<Func<T, object>>[] properties)
		{
			Properties = properties;
			return this;
		}

		public Expression<Func<T, object>>[] Properties { get; set; }
	}

	public interface IEFBatchOperationFiltered<T>
	{
		int Delete(DbConnection connection = null);
		int DeleteTop(int numberOfRows, DbConnection connection = null);
		int DeleteTopPercent(double numberOfRows, DbConnection connection = null);
		int Update<TP>(Expression<Func<T, TP>> prop, Expression<Func<T, TP>> modifier, DbConnection connection = null);
	}

	public static class EFBatchOperation
	{
		public static IEFBatchOperationBase<T> For<TContext, T>(TContext context, IDbSet<T> set)
			where TContext : DbContext
			where T : class
		{
			return EFBatchOperation<TContext, T>.For(context, set);
		}
	}

	public class EFBatchOperation<TContext, T> : IEFBatchOperationBase<T>, IEFBatchOperationFiltered<T>
		where T : class
		where TContext : DbContext
	{
		private readonly ObjectContext _context;
		private readonly DbContext _dbContext;
		private Expression<Func<T, bool>> _predicate;
		private string _deleteTopExpression;

		private EFBatchOperation(TContext context)
		{
			_dbContext = context;
			_context = (context as IObjectContextAdapter).ObjectContext;
		}

		public static IEFBatchOperationBase<T1> For<TContext1, T1>(TContext1 context, IDbSet<T1> set)
			where TContext1 : DbContext
			where T1 : class
		{
			return new EFBatchOperation<TContext1, T1>(context);
		}

		/// <summary>
		/// Bulk insert all items if the Provider supports it. Otherwise it will use the default insert unless Configuration.DisableDefaultFallback is set to true in which case it would throw an exception.
		/// </summary>
		/// <param name="items">The items to insert</param>
		/// <param name="connection">The DbConnection to use for the insert. Only needed when for example a profiler wraps the connection. Then you need to provide a connection of the type the provider use.</param>
		/// <param name="batchSize">The size of each batch. Default depends on the provider. SqlProvider uses 15000 as default</param>
		public void InsertAll<TEntity>(IEnumerable<TEntity> items, DbConnection connection = null, int? batchSize = null, int? executeTimeout = null, SqlBulkCopyOptions copyOptions = SqlBulkCopyOptions.Default, DbTransaction transaction = null)
			where TEntity : class, T
		{
			InsertAllAsync(items, connection, batchSize, executeTimeout, copyOptions, transaction).Wait();
		}

		public async Task InsertAllAsync<TEntity>(IEnumerable<TEntity> items, DbConnection connection = null, int? batchSize = null, int? executeTimeout = null, SqlBulkCopyOptions copyOptions = SqlBulkCopyOptions.Default, DbTransaction transaction = null)
			where TEntity : class, T
		{
			var con = _context.Connection as EntityConnection;
			if (con == null && connection == null)
			{
				Configuration.Log("No provider could be found because the Connection didn't implement System.Data.EntityClient.EntityConnection");
				Fallbacks.DefaultInsertAll(_context, items);
				return;
			}

			var connectionToUse = connection ?? con.StoreConnection;
			var currentType = typeof(TEntity);
			var provider = Configuration.Providers.FirstOrDefault(p => p.CanHandle(connectionToUse));

			if (provider != null && provider.CanInsert)
			{
				var mapping = EfMappingFactory.GetMappingsForContext(_dbContext);
				var typeMapping = mapping.TypeMappings[typeof(T)];
				var tableMapping = typeMapping.TableMappings.First();

                var entitySet = _context.CreateObjectSet<TEntity>().EntitySet;
                var fields = entitySet.ElementType.Properties;

                var properties = tableMapping.PropertyMappings
					.Where(p => currentType.IsSubclassOf(p.ForEntityType) || p.ForEntityType == currentType)
					.Where(p => p.IsComputed == false)
					.Select(p => new ColumnMapping
                    {
                        NameInDatabase = p.ColumnName,
                        NameOnObject = p.PropertyName,
                        IsPrimaryKey = p.IsPrimaryKey,
                        DataType = p.DataType,
                        DataTypeFull = p.DataTypeFull,
                        IsComputed = p.IsComputed,
                        IsGeneratedId = p.IsGeneratedId,
                        DefaultVal = fields[p.PropertyName].DefaultValue
                    }).ToList();

				if (tableMapping.TphConfiguration != null)
				{
					properties.Add(new ColumnMapping
					{
						NameInDatabase = tableMapping.TphConfiguration.ColumnName,
						StaticValue = tableMapping.TphConfiguration.Mappings[typeof(TEntity)]
					});
				}

				await provider.InsertItems(items, tableMapping.Schema, tableMapping.TableName, properties, connectionToUse, batchSize, executeTimeout, transaction, copyOptions);
				return;
			}

			Configuration.Log("Found provider: " + (provider?.GetType().Name ?? "[]") + " for " + connectionToUse.GetType().Name);
			Fallbacks.DefaultInsertAll(_context, items);
		}

		public void UpdateAll<TEntity>(IEnumerable<TEntity> items, Action<UpdateSpecification<TEntity>> updateSpecification, DbConnection connection = null, int? batchSize = null, int? executeTimeout = null, SqlBulkCopyOptions copyOptions = SqlBulkCopyOptions.Default, DbTransaction transaction = null) where TEntity : class, T
		{
			UpdateAllAsync(items, updateSpecification, connection, batchSize, executeTimeout, copyOptions, transaction).Wait();
		}

		public async Task UpdateAllAsync<TEntity>(IEnumerable<TEntity> items, Action<UpdateSpecification<TEntity>> updateSpecification, DbConnection connection = null, int? batchSize = null, int? executeTimeout = null, SqlBulkCopyOptions copyOptions = SqlBulkCopyOptions.Default, DbTransaction transaction = null) where TEntity : class, T
		{
			var con = _context.Connection as EntityConnection;
			if (con == null && connection == null)
			{
				throw new InvalidOperationException("No provider supporting the Update operation for this datasource was found");
			}

			var connectionToUse = connection ?? con.StoreConnection;
			var currentType = typeof(TEntity);
			var provider = Configuration.Providers.FirstOrDefault(p => p.CanHandle(connectionToUse));

			if (provider == null && con != null)
			{
				provider = Configuration.Providers.FirstOrDefault(p => p.CanHandle(con.StoreConnection));
			}

			if (provider != null && provider.CanBulkUpdate)
			{
				var mapping = EfMappingFactory.GetMappingsForContext(_dbContext);
				var typeMapping = mapping.TypeMappings[typeof(T)];
				var tableMapping = typeMapping.TableMappings.First();

                var entitySet = _context.CreateObjectSet<TEntity>().EntitySet;
                var fields = entitySet.ElementType.Properties;

                var properties = tableMapping.PropertyMappings
					.Where(p => currentType.IsSubclassOf(p.ForEntityType) || p.ForEntityType == currentType)
					.Where(p => p.IsComputed == false)
					.Select(p => new ColumnMapping
					{
						NameInDatabase = p.ColumnName,
						NameOnObject = p.PropertyName,
						DataType = p.DataType,
						DataTypeFull = p.DataTypeFull,
						IsPrimaryKey = p.IsPrimaryKey,
                        IsComputed = p.IsComputed,
                        IsGeneratedId = p.IsGeneratedId,
                        DefaultVal = fields[p.PropertyName].DefaultValue
                    }).ToList();

				var spec = new UpdateSpecification<TEntity>();
				updateSpecification(spec);
				var insertConnection = !(connectionToUse is SqlConnection) && con?.StoreConnection is SqlConnection ? con.StoreConnection : connectionToUse;
				await provider.UpdateItems(items, tableMapping.Schema, tableMapping.TableName, properties, connectionToUse, batchSize, spec, executeTimeout, transaction, insertConnection, copyOptions);
				return;
			}

			Configuration.Log("Found provider: " + (provider?.GetType().Name ?? "[]") + " for " + connectionToUse.GetType().Name);
			throw new InvalidOperationException("No provider supporting the Update operation for this datasource was found");
		}

		public IEFBatchOperationFiltered<T> Where(Expression<Func<T, bool>> predicate)
		{
			_predicate = predicate;
			return this;
		}

		public int DeleteTop(int numberOfRows, DbConnection connection = null)
		{
			_deleteTopExpression = $"TOP ({numberOfRows})";
			return Delete(connection);
		}

		public int DeleteTopPercent(double percentOfRows, DbConnection connection = null)
		{
			_deleteTopExpression = $"TOP ({percentOfRows.ToString(CultureInfo.InvariantCulture)}) PERCENT";
			return Delete(connection);
		}

		public int Delete(DbConnection connection = null)
		{
			var con = _context.Connection as EntityConnection;
			if (con == null && connection == null)
			{
				Configuration.Log("No provider could be found because the Connection didn't implement System.Data.EntityClient.EntityConnection");
				return Fallbacks.DefaultDelete(_context, _predicate);
			}
			var connectionToUse = connection ?? con.StoreConnection;

			var provider = Configuration.Providers.FirstOrDefault(p => p.CanHandle(connectionToUse));
			if (provider != null && provider.CanDelete)
			{
				var set = _context.CreateObjectSet<T>();
				var query = (ObjectQuery<T>)set.Where(_predicate);
				var queryInformation = provider.GetQueryInformation(query);
				queryInformation.TopExpression = _deleteTopExpression;

				var delete = provider.GetDeleteQuery(queryInformation);
				var parameters = query.Parameters.Select(p => new SqlParameter { Value = p.Value, ParameterName = p.Name }).ToArray<object>();
				return _context.ExecuteStoreCommand(delete, parameters);
			}

			Configuration.Log("Found provider: " + (provider?.GetType().Name ?? "[]") + " for " + connectionToUse.GetType().Name);
			return Fallbacks.DefaultDelete(_context, _predicate);
		}

		public int Update<TP>(Expression<Func<T, TP>> prop, Expression<Func<T, TP>> modifier, DbConnection connection = null)
		{
			var con = _context.Connection as EntityConnection;
			if (con == null && connection == null)
			{
				Configuration.Log("No provider could be found because the Connection didn't implement System.Data.EntityClient.EntityConnection");
				return Fallbacks.DefaultUpdate(_context, _predicate, prop, modifier);
			}
			var connectionToUse = connection ?? con.StoreConnection;

			var provider = Configuration.Providers.FirstOrDefault(p => p.CanHandle(connectionToUse));
			if (provider != null && provider.CanUpdate)
			{
				var set = _context.CreateObjectSet<T>();

				var query = (ObjectQuery<T>)set.Where(_predicate);
				var queryInformation = provider.GetQueryInformation(query);

				var updateExpression = ExpressionHelper.CombineExpressions(prop, modifier);

				var mquery = (ObjectQuery<T>)_context.CreateObjectSet<T>().Where(updateExpression);
				var mqueryInfo = provider.GetQueryInformation(mquery);

				var update = provider.GetUpdateQuery(queryInformation, mqueryInfo);

				var parameters = query.Parameters
					.Concat(mquery.Parameters)
					.Select(p => new SqlParameter { Value = p.Value, ParameterName = p.Name })
					.ToArray<object>();

				return _context.ExecuteStoreCommand(update, parameters);
			}

			Configuration.Log("Found provider: " + (provider?.GetType().Name ?? "[]") + " for " + connectionToUse.GetType().Name);
			return Fallbacks.DefaultUpdate(_context, _predicate, prop, modifier);
		}
	}
}
