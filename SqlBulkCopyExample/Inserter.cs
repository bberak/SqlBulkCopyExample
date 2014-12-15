﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Dapper;
using System.Dynamic;

namespace SqlBulkCopyExample
{
    public interface IInserter<T>
    {
        string TableName { get; }

        T AfterAutoValuesRetrieved(T src, IDictionary<string, object> autoValues);

        IEnumerable<T> Insert(IEnumerable<T> items, IDbConnection conn, IDbTransaction externalTransaction = null);
    }

    public abstract class BaseInserter<T> : IInserter<T>
    {
        public string TableName { get; private set; }

        private readonly List<ColumnMapping<T>> Mappings;

        public BaseInserter(string tableName)
        {
            TableName = tableName;
            Mappings = new List<ColumnMapping<T>> { };
        }

        public abstract T AfterAutoValuesRetrieved(T src, IDictionary<string, object> autoValues);
        
        public IEnumerable<T> Insert(IEnumerable<T> items, IDbConnection conn, IDbTransaction externalTransaction = null)
        {
            if (items == null || items.Any() == false)
                return items;

            if (externalTransaction != null && externalTransaction.Connection != conn)
                throw new InvalidOperationException("The transaction was started by a different connection");

            var columns = Mappings.Where(x => !x.IsAutoGenerated);
            var autoColumns = Mappings.Where(x => x.IsAutoGenerated);

            if (autoColumns.Any())
                return ExecuteInsert(items, columns, autoColumns, conn, externalTransaction);
            else
                ExecuteInsert(items, columns, conn, externalTransaction);

            return items;
        }

        protected virtual int ExecuteInsert(IEnumerable<T> items, IEnumerable<ColumnMapping<T>> columns, IDbConnection conn, IDbTransaction externalTransaction = null)
        {
            var insert = String.Format("{0} {1} {2};", BeginInsertStatement(), ListColumns(columns), ListValues(columns));
            var result = 0;
            var columnList = columns.ToList();
            var transformedItems = items.Select(x =>
            {
                var expando = new ExpandoObject { };
                columnList.ForEach(y => y.MapToRow(expando, x));
                return expando;
            })
            .ToList();

            using (var transaction = externalTransaction ?? conn.BeginTransaction())
            {
                result = conn.Execute(insert, transformedItems, transaction);

                transaction.Commit();
            }

            return result;
        }

        protected virtual IEnumerable<T> ExecuteInsert(
            IEnumerable<T> items, 
            IEnumerable<ColumnMapping<T>> columns,
            IEnumerable<ColumnMapping<T>> autoColumns,
            IDbConnection conn, 
            IDbTransaction externalTransaction = null)
        {
            var rng = new Random(Environment.TickCount);
            var tempTable = String.Format("{0}_{1}", TableName, rng.Next(0, 10000000));
            var insertStatement = String.Format("{0} {1} {2} {3};", 
                BeginInsertStatement(),
                ListColumns(columns),
                InsertIntoTempTable(tempTable, autoColumns),
                ListValues(columns));
            var columnList = columns.ToList();
            var transformedItems = items.Select(x =>
            {
                var expando = new ExpandoObject { };
                columnList.ForEach(y => y.MapToRow(expando, x));
                return expando;
            })
           .ToList();

            IDictionary<string, object>[] results = null;

            using (var transaction = externalTransaction ?? conn.BeginTransaction())
            {
                conn.Execute(CreateTempTable(tempTable, autoColumns), null, transaction);

                conn.Execute(insertStatement, transformedItems, transaction);

                results = conn.Query(SelectTempTable(tempTable), null, transaction).Select(d => d as IDictionary<string, object>).ToArray();

                if (results == null || results.Any() == false)
                    throw new DataException("Failed to retrieve any results from the INSERT");

                if (results.Count() != items.Count())
                    throw new DataException("Received the incorrect number of results from the INSERT");
                
                items = items.Select((x, idx)
                    => AfterAutoValuesRetrieved(x, results[idx]))
                    .ToList();

                conn.Execute(DropTempTable(tempTable), null, transaction);

                transaction.Commit();
            }

            return items;
        }

        protected virtual string CreateTempTable(string name, IEnumerable<ColumnMapping<T>> autoColumns)
        {
            var r= String.Format(@"
                IF OBJECT_ID('tempdb..#{0}') IS NOT NULL 
                    BEGIN 
                        DROP TABLE #{0} 
                    END;               
                CREATE TABLE #{0} ({1})", 
                name,
                autoColumns.ToString(x => x.DbColumnName + " " + x.DbType));

            return r;
        }

        protected virtual string BeginInsertStatement()
        {
            return String.Format("INSERT INTO {0}", TableName);
        }

        protected virtual string ListColumns(IEnumerable<ColumnMapping<T>> columns)
        {
            return String.Format("({0})", columns.ToString(x => x.DbColumnName));
        }

        protected virtual string InsertIntoTempTable(string name, IEnumerable<ColumnMapping<T>> autoColumns)
        {
            return String.Format("OUTPUT {0} INTO #{1} ({2})",
                autoColumns.ToString(x => "INSERTED." + x.DbColumnName),
                name,
                autoColumns.ToString(x => x.DbColumnName));
        }

        protected virtual string ListValues(IEnumerable<ColumnMapping<T>> columns)
        {
            return String.Format("VALUES ({0})", columns.ToString(x => "@" + x.DbColumnName));
        }

        protected virtual string SelectTempTable(string name)
        {
            return String.Format("SELECT * FROM #{0}", name);
        }

        protected virtual string DropTempTable(string name)
        {
            return String.Format("DROP TABLE #{0};", name);
        }
        
        protected ColumnMapping<T> Column<TSelector>(string dbColumn, Func<T, TSelector> map = null)
        {
            Action<IDictionary<string, object>, T> mapToRow = (d, t) => d[dbColumn] = map(t);

            var mapping = new ColumnMapping<T>(dbColumn, mapToRow, isAutoGenerated: false, dbType: null);

            Mappings.Add(mapping);

            return mapping;
        }

        protected ColumnMapping<T> Column(string dbColumn, string dbType)
        {
            var mapping = new ColumnMapping<T>(dbColumn, mapToRow: null, isAutoGenerated: true, dbType: dbType);

            Mappings.Add(mapping);

            return mapping;
        }

        private static PropertyInfo GetPropertyInfo<TProperty>(Expression<Func<T, TProperty>> propertyLambda)
        {
            var type = typeof(T);

            var member = propertyLambda.Body as MemberExpression;
            if (member == null)
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a method, not a property.",
                    propertyLambda.ToString()));

            var propInfo = member.Member as PropertyInfo;
            if (propInfo == null)
                throw new ArgumentException(string.Format(
                    "Expression '{0}' refers to a field, not a property.",
                    propertyLambda.ToString()));

            if (type != propInfo.ReflectedType &&
                !type.IsSubclassOf(propInfo.ReflectedType))
                throw new ArgumentException(string.Format(
                    "Expresion '{0}' refers to a property that is not from type {1}.",
                    propertyLambda.ToString(),
                    type));

            return propInfo;
        }
    }

    public class ColumnMapping<T>
    {
        public string DbColumnName { get; private set; }

        public bool IsAutoGenerated { get; private set; }

        public string DbType { get; private set; }

        public Action<IDictionary<string, object>, T> MapToRow { get; private set; }

        public ColumnMapping(string dbColumnName, Action<IDictionary<string, object>, T> mapToRow = null, bool isAutoGenerated = false, string dbType = null)
        {
            DbColumnName = dbColumnName;
            MapToRow = mapToRow;
            IsAutoGenerated = isAutoGenerated;
            DbType = dbType;
        }
    }

    public static class ColumnMappingExt
    {
        public static string ToString<T>(this IEnumerable<ColumnMapping<T>> items, Func<ColumnMapping<T>, string> format, string joiner = ", ")
        {
            if (items == null)
                return null;

            return String.Join(joiner, items.Select(format).ToArray());
        }
    }
}
