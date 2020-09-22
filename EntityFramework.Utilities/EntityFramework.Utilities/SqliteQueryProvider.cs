using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Transactions;

namespace EntityFramework.Utilities
{
    public class SqliteQueryProvider : IQueryProvider
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
                SQLiteCommand sqlComm;
                try
                {
                    DataTable dt = items.ToDataTable(properties);

                    sqlComm = new SQLiteCommand("SAVEPOINT Batch;")
                    {
                        Connection = storeConnection as SQLiteConnection,
                        Transaction = transaction as SQLiteTransaction2
                    };
                    await sqlComm.ExecuteNonQueryAsync();

                    string columns = string.Join(",", properties.Select(c => c.NameInDatabase));
                    string values = string.Join(",", properties.Select(c => string.Format("@{0}", c.NameInDatabase)));
                    String sqlCommandInsert = string.Format("INSERT INTO {3}[{2}] ({0}) VALUES ({1})", columns, values, tableName, !string.IsNullOrEmpty(schema) ? "[" + schema + "]." : "");
                    
                    int inserted = 0;
                    // set default batch size 100k if rows > 100k
                    batchSize = dt.Rows.Count > 100000 ? 100000 : batchSize;
                    // check if batchsize is null
                    batchSize = batchSize ?? dt.Rows.Count;
                    int nCount = 0;
                    for (int m = 0; nCount < dt.Rows.Count; m++)
                    {
                        sqlComm = new SQLiteCommand("SAVEPOINT Chunk;")
                        {
                            Connection = storeConnection as SQLiteConnection,
                            Transaction = transaction as SQLiteTransaction2
                        };
                        await sqlComm.ExecuteNonQueryAsync();
                        //---INSIDE LOOP

                        // set the insert command
                        sqlComm = new SQLiteCommand(sqlCommandInsert)
                        {
                            Connection = storeConnection as SQLiteConnection,
                            Transaction = transaction as SQLiteTransaction2
                        };

                        for (int n = 0; n < batchSize; n++)
                        {
                            nCount = ((m * batchSize.Value) + n);
                            if (nCount == dt.Rows.Count)
                            {
                                break;
                            }
                            DataRow row = dt.Rows[nCount];
                            sqlComm.Parameters.Clear();
                            foreach (DataColumn col in dt.Columns)
                            {
                                var val = row[col];
                                // for primary keys with autoincrement
                                if (properties.Where(c => c.IsPrimaryKey && c.IsGeneratedId && c.NameInDatabase.Equals(col.ColumnName, StringComparison.CurrentCultureIgnoreCase)).Count() > 0)
                                {
                                    val = null;
                                }
                                sqlComm.Parameters.AddWithValue("@" + col.ColumnName, val);
                            }
                            inserted += await sqlComm.ExecuteNonQueryAsync();
                        }

                        //---END LOOP, SAVE THE CHUNK
                        sqlComm = new SQLiteCommand("RELEASE SAVEPOINT Chunk;")
                        {
                            Connection = storeConnection as SQLiteConnection,
                            Transaction = transaction as SQLiteTransaction2
                        };
                        await sqlComm.ExecuteNonQueryAsync();
                    }
                    var nRowUpdatedCount = inserted;

                    //---SAVE THE BATCH
                    sqlComm = new SQLiteCommand("RELEASE SAVEPOINT Batch;")
                    {
                        Connection = storeConnection as SQLiteConnection,
                        Transaction = transaction as SQLiteTransaction2
                    };
                    await sqlComm.ExecuteNonQueryAsync();
                }
                catch(Exception ex)
                {
                    string strError = ex.Message + ", " + ex.InnerException;
                    //m_logger.DebugFormat("InsertItems failed: {0}", strError);
                    //---ROLLBACK
                    sqlComm = new SQLiteCommand("ROLLBACK TRANSACTION TO SAVEPOINT Batch;")
                    {
                        Connection = storeConnection as SQLiteConnection,
                        Transaction = transaction as SQLiteTransaction2
                    };
                    await sqlComm.ExecuteNonQueryAsync();

                    throw ex;
                }
            }
        }

		public async Task UpdateItems<T>(IEnumerable<T> items, string schema, string tableName, IList<ColumnMapping> properties, DbConnection storeConnection, int? batchSize, UpdateSpecification<T> updateSpecification, int? executeTimeout, DbTransaction transaction, DbConnection insertConnection, object copyOptions)
        {
            if (storeConnection.State != System.Data.ConnectionState.Open)
            {
                storeConnection.Open();
            }

            SQLiteCommand sqlComm;
            try
            {
                // ToDo: once System.Data.SQLite is updated to include SQLite core v3.33
                // we can use the "Update table1 Select From table2" syntax
                //-------------------------------
                // for now < SQLite v3.33.0, we just perform above sqlite update with repetitive subquery, e.g.,
                // UPDATE table1 SET table1col1 =
                //   (SELECT TempTable.col1
                //    FROM TempTable
                //    WHERE table1.Id = TempTable.Id), 
                // table1col2 =
                //   (SELECT TempTable.col2
                //    FROM TempTable
                //    WHERE table1.Id = TempTable.Id);
                // ------------------------------
                var tempTableName = "#" + Guid.NewGuid().ToString("N");
                var columnsToUpdate = updateSpecification.Properties.Select(p => p.GetPropertyName()).ToDictionary(x => x);
                var filtered = properties.Where(p => columnsToUpdate.ContainsKey(p.NameOnObject) || p.IsPrimaryKey).
                    // we need to set IsGeneratedId as false to ensure the temptable ID values are inserted properly
                    Select(x => {
                        if (x.IsPrimaryKey)
                        {
                            x.IsGeneratedId = false;
                        };
                        return x;
                    }).ToList();
                var columns = filtered.Select(c => "[" + c.NameInDatabase + "] " + c.DataTypeFull + (c.DataType.EndsWith("char") ? " COLLATE DATABASE_DEFAULT" : null));
                var pkConstraint = string.Join(", ", properties.Where(p => p.IsPrimaryKey).Select(c => "[" + c.NameInDatabase + "]"));

                var str = "";
                str = string.Format(@"CREATE TABLE {0}[{1}]({2}, PRIMARY KEY ({3}))", (!string.IsNullOrEmpty(schema) ? "[" + schema + "]." : ""), tempTableName, string.Join(", ", columns), pkConstraint);

                // create the ff:
                // Table1.pk1 = TempTable.pk1 and Table1.pk2 = tempTable.pk2 ...
                var pks = string.Join(" and ", properties.Where(p => p.IsPrimaryKey).Select(x => (!string.IsNullOrEmpty(schema) ? "[" + schema + "]." : "") + "[" + tableName + "].[" + x.NameInDatabase + "] = " + (!string.IsNullOrEmpty(schema) ? "[" + schema + "]." : "") + "[" + tempTableName + "].[" + x.NameInDatabase + "]"));

                // create the ff:
                // Table1.col1 = (SELECT col1 FROM TempTable WHERE /*pks*/), table1.col2 = (SELECT.col2 FROM TempTable WHERE /*pks*/)
                var settersTo = string.Join(",", filtered.Where(c => !c.IsPrimaryKey).Select(c => "[" + c.NameInDatabase + "] = " + 
                    "(SELECT [" + c.NameInDatabase + "] FROM [" + tempTableName + "] WHERE " + pks + ")"));

                var mergeCommand = string.Format(@"UPDATE {0}[{1}] SET {2}",
                           !string.IsNullOrEmpty(schema) ? "[" + schema + "]." : "", tableName, settersTo);

                using (var createCommand = storeConnection.CreateCommand())
                using (var mCommand = storeConnection.CreateCommand())
                using (var dCommand = storeConnection.CreateCommand())
                {
                    sqlComm = new SQLiteCommand("SAVEPOINT BatchUpd;")
                    {
                        Connection = storeConnection as SQLiteConnection,
                        Transaction = transaction as SQLiteTransaction2
                    };
                    await sqlComm.ExecuteNonQueryAsync();
                    
                    createCommand.CommandText = str;
                    mCommand.CommandText = mergeCommand;
                    dCommand.CommandText = string.Format(@"DROP table {0}[{1}]", (!string.IsNullOrEmpty(schema) ? "[" + schema + "]." : ""), tempTableName);

                    createCommand.CommandTimeout = executeTimeout ?? 600;
                    mCommand.CommandTimeout = executeTimeout ?? 600;
                    dCommand.CommandTimeout = executeTimeout ?? 600;

                    createCommand.Transaction = transaction;
                    mCommand.Transaction = transaction;
                    dCommand.Transaction = transaction;

                    await createCommand.ExecuteNonQueryAsync();
                    await InsertItems(items, schema, tempTableName, filtered, insertConnection, batchSize, executeTimeout, transaction, copyOptions);
                    await mCommand.ExecuteNonQueryAsync();
                    await dCommand.ExecuteNonQueryAsync();

                    sqlComm = new SQLiteCommand("RELEASE SAVEPOINT BatchUpd;")
                    {
                        Connection = storeConnection as SQLiteConnection,
                        Transaction = transaction as SQLiteTransaction2
                    };
                    await sqlComm.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                string strError = ex.Message + ", " + ex.InnerException;
                //m_logger.DebugFormat("InsertItems failed: {0}", strError);
                //---ROLLBACK
                sqlComm = new SQLiteCommand("ROLLBACK TRANSACTION TO SAVEPOINT BatchUpd;")
                {
                    Connection = storeConnection as SQLiteConnection,
                    Transaction = transaction as SQLiteTransaction2
                };
                sqlComm.ExecuteNonQuery();

                throw ex;
            }
        }

		public bool CanHandle(DbConnection storeConnection)
		{
			return storeConnection is SQLiteConnection;
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

    public static class DataTableHelpers
    {
        /// <summary>
        /// Check if column data type is numeric
        /// </summary>
        /// <param name="col"></param>
        /// <returns></returns>
        public static bool IsNumeric(this DataColumn col)
        {
            if (col != null)
            {
                // Make this const
                var numericTypes = new[] { typeof(Byte), typeof(Decimal), typeof(Double),
                typeof(Int16), typeof(Int32), typeof(Int64), typeof(SByte),
                typeof(Single), typeof(UInt16), typeof(UInt32), typeof(UInt64)};
                return numericTypes.Contains(col.DataType);
            }
            return false;
        }

        /// <summary>
        /// Check if column data type is alpha
        /// </summary>
        /// <param name="col"></param>
        /// <returns></returns>
        public static bool IsString(this DataColumn col)
        {
            if (col != null)
            {
                // Make this const
                var alphaTypes = new[] { typeof(Char), typeof(String) };
                return alphaTypes.Contains(col.DataType);
            }
            return false;
        }

        /// <summary>
        /// Check if column data type is time based
        /// </summary>
        /// <param name="col"></param>
        /// <returns></returns>
        public static bool IsDateTime(this DataColumn col)
        {
            if (col != null)
            {
                // Make this const
                var timebasedTypes = new[] { typeof(DateTime) };
                return timebasedTypes.Contains(col.DataType);
            }
            return false;
        }

        /// <summary>
        /// Check if column data type is boolean
        /// </summary>
        /// <param name="col"></param>
        /// <returns></returns>
        public static bool IsBoolean(this DataColumn col)
        {
            if (col != null)
            {
                // Make this const
                var boolTypes = new[] { typeof(Boolean) };
                return boolTypes.Contains(col.DataType);
            }
            return false;
        }

        /// <summary>
        /// Check if column data type is blob
        /// </summary>
        /// <param name="col"></param>
        /// <returns></returns>
        public static bool IsBlob(this DataColumn col)
        {
            if (col != null)
            {
                // Make this const
                var blobTypes = new[] { typeof(Byte[]) };
                return blobTypes.Contains(col.DataType);
            }
            return false;
        }

        /// <summary>
        /// Converts a List to a DataTable
        /// </summary>
        public interface IDataValuesProvider
        {
            IEnumerable<object> GetDataValues(bool includeAll);
        }
        // Sample for using IDataValuesProvider
        //... on the class, implement GetDataValues():
        //public class StockItem : IDataValuesProvider
        //{
        //    public int Id { get; set; }
        //    public string ItemName { get; set; }
        //    [Browsable(false), DisplayName("Ignore")]
        //    public string propA { get; set; }
        //    [ReadOnly(true)]
        //    public string Zone { get; set; }
        //    public string Size { get; set; }
        //    [DisplayName("Nullable")]
        //    public int? Foo { get; set; }
        //    public int OnHand { get; set; }
        //    public string ProdCode { get; set; }
        //    [Browsable(false)]
        //    public string propB { get; set; }
        //    public DateTime ItemDate { get; set; }
        //
        //    // IDataValuesProvider implementation
        //    public IEnumerable<object> GetDataValues(bool IncludeAll)
        //    {
        //        List<object> values = new List<object>();
        //
        //        values.AddRange(new object[] { Id, ItemName });
        //        if (IncludeAll) values.Add(propA);
        //
        //        values.AddRange(new object[] { Zone, Size, Foo, OnHand, ProdCode });
        //        if (IncludeAll) values.Add(propB);
        //
        //        values.Add(ItemDate);
        //
        //        return values;
        //    }
        //}

        public static DataTable ToDataTable<T,T1>(this IEnumerable<T> lst, IList<T1> prop, bool includeAll = true)
        {
            DataTable dt = new DataTable();
            DataColumn dc;
            PropertyDescriptor pd;
            bool Browsable;

            PropertyDescriptorCollection propCol = TypeDescriptor.GetProperties(typeof(T));

            for (int n = 0; n < propCol.Count; n++)
            {
                pd = propCol[n];
                Type propT = pd.PropertyType;

                dc = new DataColumn(pd.Name);

                // if Nullable, get underlying type
                // the first test may not be needed
                if (propT.IsGenericType && Nullable.GetUnderlyingType(propT) != null)
                {
                    propT = Nullable.GetUnderlyingType(propT);
                    dc.DataType = propT;
                    dc.AllowDBNull = true;
                }
                else
                {
                    dc.DataType = propT;
                    dc.AllowDBNull = false;
                }

                // is it readonly?
                if (pd.Attributes[typeof(ReadOnlyAttribute)] != null)
                {
                    dc.ReadOnly = ((ReadOnlyAttribute)pd.
                             Attributes[typeof(ReadOnlyAttribute)]).IsReadOnly;
                }

                // DefaultValue ...
                if (pd.Attributes[typeof(DefaultValueAttribute)] != null)
                {
                    dc.DefaultValue = ((DefaultValueAttribute)pd.
                           Attributes[typeof(DefaultValueAttribute)]).Value;
                }

                // caption / display name
                dc.ExtendedProperties.Add("DisplayName", dc.Caption);
                if (pd.Attributes[typeof(DisplayNameAttribute)] != null)
                {
                    // these are usually present but blank
                    string theName = ((DisplayNameAttribute)pd.
                           Attributes[typeof(DisplayNameAttribute)]).DisplayName;
                    dc.Caption = string.IsNullOrEmpty(theName) ? dc.Caption : theName;
                    // DGV doesnt use Caption...save for later
                    dc.ExtendedProperties["DisplayName"] = dc.Caption;
                }

                Browsable = true;
                dc.ExtendedProperties.Add("Browsable", Browsable);
                var foo = pd.Attributes[typeof(BrowsableAttribute)];
                if (pd.Attributes[typeof(BrowsableAttribute)] != null)
                {
                    Browsable = ((BrowsableAttribute)pd.Attributes[typeof(BrowsableAttribute)]).Browsable;
                    // no such thing as a NonBrowsable DataColumn
                    dc.ExtendedProperties["Browsable"] = Browsable;
                }

                // ToDo: add support for custom attributes

                if (includeAll || Browsable)
                {
                    dt.Columns.Add(dc);
                }
            }

            // the lst could be empty such as creating a typed table
            if (lst.Count() == 0) return dt;

            if (lst.First() is IDataValuesProvider)
            {
                IDataValuesProvider dvp;
                // copy the data - let the class do the work
                foreach (T item in lst)
                {
                    dvp = (IDataValuesProvider)item;
                    dt.Rows.Add(dvp.GetDataValues(includeAll).ToArray());
                }
            }
            else
            {
                List<object> values;
                foreach (T item in lst)
                {
                    values = new List<object>();
                    // only Browsable columns added
                    for (int n = 0; n < dt.Columns.Count; n++)
                    {
                        var dtype = dt.Columns[n].DataType;
                        var val = propCol[dt.Columns[n].ColumnName].GetValue(item);
                        
                        if (dtype.Name.Equals("datetime", StringComparison.CurrentCultureIgnoreCase))
                        {
                            // we need to check if the datetime is in UTC
                            if ((val as DateTime?).HasValue ? (val as DateTime?).Value.Kind == DateTimeKind.Utc : false)
                            {
                                dt.Columns[n].DateTimeMode = DataSetDateTime.Utc;
                            }
                        }
                        // if col doesn't allow null and val is null
                        // set to default val depending on data type
                        if (!dt.Columns[n].AllowDBNull && (val == null))
                        {
                            var temp = (prop as IList<ColumnMapping>).Where(x => x.NameInDatabase.Equals(dt.Columns[n].ColumnName)).FirstOrDefault();
                            if (temp != null && (temp as ColumnMapping).DefaultVal != null)
                            {
                                val = (temp as ColumnMapping).DefaultVal;
                            }
                            else if (dt.Columns[n].IsString())
                            {
                                val = "";
                            }
                            else if (dt.Columns[n].IsNumeric())
                            {
                                val = 0;
                            }
                            else if (dt.Columns[n].IsBoolean())
                            {
                                val = false;
                            }
                            else if (dt.Columns[n].IsDateTime())
                            {
                                val = DateTime.MinValue;
                            }
                            else if (dt.Columns[n].IsBlob())
                            {
                                val = new Byte[] { 0 };
                            }
                        }
                        values.Add(val);
                    }
                    dt.Rows.Add(values.ToArray());
                }
            }

            return dt;
        }
    }
}
