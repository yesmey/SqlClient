// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.Data.Common;

namespace Microsoft.Data.SqlClient
{
    // -------------------------------------------------------------------------------------------------
    // this class helps allows the user to create association between source and target columns
    /// <summary>
    /// 
    /// </summary>
    public sealed class SqlBulkCopyOrderHintColumn
    {
        private SortOrder _sortOrder;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="column"></param>
        /// <param name="sortOrder"></param>
        public SqlBulkCopyOrderHintColumn(string column, SortOrder sortOrder)
        {
            Column = column;
            SortOrder = sortOrder;
        }

        /// <summary>
        /// 
        /// </summary>
        public string Column { get; set; }

        /// <summary>
        /// 
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public SortOrder SortOrder
        {
            get => _sortOrder;
            set
            {
                if (value == SortOrder.Unspecified)
                    throw ADP.Argument("");
                _sortOrder = value;
            }
        }
    }
}
