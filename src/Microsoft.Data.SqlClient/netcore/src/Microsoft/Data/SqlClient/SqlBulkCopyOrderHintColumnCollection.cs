// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Diagnostics;

namespace Microsoft.Data.SqlClient
{
    /// <summary>
    /// 
    /// </summary>
    public sealed class SqlBulkCopyOrderHintColumnCollection : CollectionBase
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="index"></param>
        public SqlBulkCopyOrderHintColumn this[int index] => (SqlBulkCopyOrderHintColumn)this.List[index];

        /// <summary>
        /// 
        /// </summary>
        /// <param name="bulkCopyOrderHintColumn"></param>
        /// <returns></returns>
        public SqlBulkCopyOrderHintColumn Add(SqlBulkCopyOrderHintColumn bulkCopyOrderHintColumn)
        {
            InnerList.Add(bulkCopyOrderHintColumn);
            return bulkCopyOrderHintColumn;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="column"></param>
        /// <param name="sortOrder"></param>
        /// <returns></returns>
        public SqlBulkCopyOrderHintColumn Add(string column, SortOrder sortOrder) =>
            Add(new SqlBulkCopyOrderHintColumn(column, sortOrder));

        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool Contains(SqlBulkCopyOrderHintColumn value) => InnerList.Contains(value);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <param name="index"></param>
        public void CopyTo(SqlBulkCopyOrderHintColumn[] array, int index) => InnerList.CopyTo(array, index);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public int IndexOf(SqlBulkCopyOrderHintColumn value) => InnerList.IndexOf(value);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="index"></param>
        /// <param name="value"></param>
        public void Insert(int index, SqlBulkCopyOrderHintColumn value) => InnerList.Insert(index, value);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        public void Remove(SqlBulkCopyOrderHintColumn value) => InnerList.Remove(value);
    }
}
