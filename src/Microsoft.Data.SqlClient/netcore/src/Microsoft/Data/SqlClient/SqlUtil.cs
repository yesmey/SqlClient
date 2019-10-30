// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Data.Common;

namespace Microsoft.Data.SqlClient
{
    internal static class AsyncHelper
    {
        internal static Task CreateContinuationTask(Task task, Action onSuccess, Action<Exception> onFailure = null)
        {
            if (task == null)
            {
                onSuccess();
                return null;
            }
            else
            {
                TaskCompletionSource<object> completion = new TaskCompletionSource<object>();
                ContinueTaskWithState(task, completion,
                    state: Tuple.Create(onSuccess, onFailure, completion),
                    onSuccess: (state) =>
                    {
                        var parameters = (Tuple<Action, Action<Exception>, TaskCompletionSource<object>>)state;
                        Action success = parameters.Item1;
                        TaskCompletionSource<object> taskCompletionSource = parameters.Item3;
                        success();
                        taskCompletionSource.SetResult(null);
                    },
                    onFailure: (exception, state) =>
                    {
                        var parameters = (Tuple<Action, Action<Exception>, TaskCompletionSource<object>>)state;
                        Action<Exception> failure = parameters.Item2;
                        failure?.Invoke(exception);
                    }
                );
                return completion.Task;
            }
        }

        internal static Task CreateContinuationTaskWithState(Task task, object state, Action<object> onSuccess, Action<Exception, object> onFailure = null)
        {
            if (task == null)
            {
                onSuccess(state);
                return null;
            }
            else
            {
                var completion = new TaskCompletionSource<object>();
                ContinueTaskWithState(task, completion, state,
                    onSuccess: (continueState) =>
                    {
                        onSuccess(continueState);
                        completion.SetResult(null);
                    },
                    onFailure: onFailure
                );
                return completion.Task;
            }
        }

        internal static Task CreateContinuationTask<T1, T2>(Task task, Action<T1, T2> onSuccess, T1 arg1, T2 arg2, SqlInternalConnectionTds connectionToDoom = null, Action<Exception> onFailure = null)
        {
            return CreateContinuationTask(task, () => onSuccess(arg1, arg2), onFailure);
        }

        internal static void ContinueTask(Task task,
                TaskCompletionSource<object> completion,
                Action onSuccess,
                Action<Exception> onFailure = null,
                Action onCancellation = null,
                Func<Exception, Exception> exceptionConverter = null
            )
        {
            task.ContinueWith(
                tsk =>
                {
                    if (tsk.Exception != null)
                    {
                        Exception exc = tsk.Exception.InnerException;
                        if (exceptionConverter != null)
                        {
                            exc = exceptionConverter(exc);
                        }
                        try
                        {
                            onFailure?.Invoke(exc);
                        }
                        finally
                        {
                            completion.TrySetException(exc);
                        }
                    }
                    else if (tsk.IsCanceled)
                    {
                        try
                        {
                            onCancellation?.Invoke();
                        }
                        finally
                        {
                            completion.TrySetCanceled();
                        }
                    }
                    else
                    {
                        try
                        {
                            onSuccess();
                        }
                        catch (Exception e)
                        {
                            completion.SetException(e);
                        }
                    }
                }, TaskScheduler.Default
            );
        }

        // the same logic as ContinueTask but with an added state parameter to allow the caller to avoid the use of a closure
        // the parameter allocation cannot be avoided here and using closure names is clearer than Tuple numbered properties
        internal static void ContinueTaskWithState(Task task,
            TaskCompletionSource<object> completion,
            object state,
            Action<object> onSuccess,
            Action<Exception, object> onFailure = null,
            Action<object> onCancellation = null,
            Func<Exception, Exception> exceptionConverter = null
        )
        {
            task.ContinueWith(
                tsk =>
                {
                    if (tsk.Exception != null)
                    {
                        Exception exc = tsk.Exception.InnerException;
                        if (exceptionConverter != null)
                        {
                            exc = exceptionConverter(exc);
                        }
                        try
                        {
                            onFailure?.Invoke(exc, state);
                        }
                        finally
                        {
                            completion.TrySetException(exc);
                        }
                    }
                    else if (tsk.IsCanceled)
                    {
                        try
                        {
                            onCancellation?.Invoke(state);
                        }
                        finally
                        {
                            completion.TrySetCanceled();
                        }
                    }
                    else
                    {
                        try
                        {
                            onSuccess(state);
                        }
                        catch (Exception e)
                        {
                            completion.SetException(e);
                        }
                    }
                }, TaskScheduler.Default
            );
        }

        internal static void WaitForCompletion(Task task, int timeout, Action onTimeout = null, bool rethrowExceptions = true)
        {
            try
            {
                task.Wait(timeout > 0 ? (1000 * timeout) : Timeout.Infinite);
            }
            catch (AggregateException ae)
            {
                if (rethrowExceptions)
                {
                    Debug.Assert(ae.InnerExceptions.Count == 1, "There is more than one exception in AggregateException");
                    ExceptionDispatchInfo.Capture(ae.InnerException).Throw();
                }
            }
            if (!task.IsCompleted)
            {
                if (onTimeout != null)
                {
                    onTimeout();
                }
            }
        }

        internal static void SetTimeoutException(TaskCompletionSource<object> completion, int timeout, Func<Exception> exc, CancellationToken ctoken)
        {
            if (timeout > 0)
            {
                Task.Delay(timeout * 1000, ctoken).ContinueWith((tsk) =>
                {
                    if (!tsk.IsCanceled && !completion.Task.IsCompleted)
                    {
                        completion.TrySetException(exc());
                    }
                });
            }
        }
    }

    internal static class SQL
    {
        // The class SQL defines the exceptions that are specific to the SQL Adapter.
        // The class contains functions that take the proper informational variables and then construct
        // the appropriate exception with an error string obtained from the resource Framework.txt.
        // The exception is then returned to the caller, so that the caller may then throw from its
        // location so that the catcher of the exception will have the appropriate call stack.
        // This class is used so that there will be compile time checking of error
        // messages.  The resource Framework.txt will ensure proper string text based on the appropriate
        // locale.

        //
        // SQL specific exceptions
        //

        //
        // SQL.Connection
        //
        internal static Exception CannotGetDTCAddress()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_CannotGetDTCAddress));
        }

        internal static Exception InvalidInternalPacketSize(string str)
        {
            return ADP.ArgumentOutOfRange(str);
        }
        internal static Exception InvalidPacketSize()
        {
            return ADP.ArgumentOutOfRange(System.SRHelper.GetString(SR.SQL_InvalidTDSPacketSize));
        }
        internal static Exception InvalidPacketSizeValue()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_InvalidPacketSizeValue));
        }
        internal static Exception InvalidSSPIPacketSize()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_InvalidSSPIPacketSize));
        }
        internal static Exception AuthenticationAndIntegratedSecurity()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_AuthenticationAndIntegratedSecurity));
        }
        internal static Exception IntegratedWithUserIDAndPassword()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_IntegratedWithUserIDAndPassword));
        }
        internal static Exception InteractiveWithoutUserID()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_InteractiveWithoutUserID));
        }
        internal static Exception InteractiveWithPassword()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_InteractiveWithPassword));
        }
        internal static Exception NullEmptyTransactionName()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_NullEmptyTransactionName));
        }
        internal static Exception UserInstanceFailoverNotCompatible()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_UserInstanceFailoverNotCompatible));
        }
        internal static Exception CredentialsNotProvided(SqlAuthenticationMethod auth)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_CredentialsNotProvided, DbConnectionStringBuilderUtil.AuthenticationTypeToString(auth)));
        }
        internal static Exception ParsingErrorLibraryType(ParsingErrorState state, int libraryType)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingErrorAuthLibraryType, ((int)state).ToString(CultureInfo.InvariantCulture), libraryType));
        }
        internal static Exception InvalidSQLServerVersionUnknown()
        {
            return ADP.DataAdapter(System.SRHelper.GetString(SR.SQL_InvalidSQLServerVersionUnknown));
        }
        internal static Exception SynchronousCallMayNotPend()
        {
            return new Exception(System.SRHelper.GetString(SR.Sql_InternalError));
        }
        internal static Exception ConnectionLockedForBcpEvent()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ConnectionLockedForBcpEvent));
        }
        internal static Exception InstanceFailure()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_InstanceFailure));
        }
        internal static Exception ChangePasswordArgumentMissing(string argumentName)
        {
            return ADP.ArgumentNull(System.SRHelper.GetString(SR.SQL_ChangePasswordArgumentMissing, argumentName));
        }
        internal static Exception ChangePasswordConflictsWithSSPI()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_ChangePasswordConflictsWithSSPI));
        }
        internal static Exception ChangePasswordRequiresYukon()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ChangePasswordRequiresYukon));
        }
        internal static Exception ChangePasswordUseOfUnallowedKey(string key)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ChangePasswordUseOfUnallowedKey, key));
        }
        internal static Exception GlobalizationInvariantModeNotSupported()
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_GlobalizationInvariantModeNotSupported));
        }

        //
        // Global Transactions.
        //
        internal static Exception GlobalTransactionsNotEnabled()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.GT_Disabled));
        }
        internal static Exception UnknownSysTxIsolationLevel(System.Transactions.IsolationLevel isolationLevel)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_UnknownSysTxIsolationLevel, isolationLevel.ToString()));
        }


        internal static Exception InvalidPartnerConfiguration(string server, string database)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_InvalidPartnerConfiguration, server, database));
        }

        internal static Exception BatchedUpdateColumnEncryptionSettingMismatch()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_BatchedUpdateColumnEncryptionSettingMismatch, "SqlCommandColumnEncryptionSetting", "SelectCommand", "InsertCommand", "UpdateCommand", "DeleteCommand"));
        }
        internal static Exception MARSUnspportedOnConnection()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_MarsUnsupportedOnConnection));
        }

        internal static Exception CannotModifyPropertyAsyncOperationInProgress([CallerMemberName] string property = "")
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_CannotModifyPropertyAsyncOperationInProgress, property));
        }
        internal static Exception NonLocalSSEInstance()
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_NonLocalSSEInstance));
        }

        // SQL.ActiveDirectoryAuth
        //
        internal static Exception UnsupportedAuthentication(string authentication)
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_UnsupportedAuthentication, authentication));
        }

        internal static Exception UnsupportedSqlAuthenticationMethod(SqlAuthenticationMethod authentication)
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_UnsupportedSqlAuthenticationMethod, authentication));
        }

        internal static Exception CannotCreateAuthProvider(string authentication, string type, Exception e)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_CannotCreateAuthProvider, authentication, type), e);
        }

        internal static Exception CannotCreateSqlAuthInitializer(string type, Exception e)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_CannotCreateAuthInitializer, type), e);
        }

        internal static Exception CannotInitializeAuthProvider(string type, Exception e)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_CannotInitializeAuthProvider, type), e);
        }

        internal static Exception UnsupportedAuthenticationByProvider(string authentication, string type)
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_UnsupportedAuthenticationByProvider, type, authentication));
        }

        internal static Exception CannotFindAuthProvider(string authentication)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_CannotFindAuthProvider, authentication));
        }

        internal static Exception CannotGetAuthProviderConfig(Exception e)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_CannotGetAuthProviderConfig), e);
        }

        internal static Exception ParameterCannotBeEmpty(string paramName)
        {
            return ADP.ArgumentNull(System.SRHelper.GetString(SR.SQL_ParameterCannotBeEmpty, paramName));
        }

        //
        // SQL.DataCommand
        //

        internal static ArgumentOutOfRangeException NotSupportedEnumerationValue(Type type, int value)
        {
            return ADP.ArgumentOutOfRange(System.SRHelper.GetString(SR.SQL_NotSupportedEnumerationValue, type.Name, value.ToString(System.Globalization.CultureInfo.InvariantCulture)), type.Name);
        }

        internal static ArgumentOutOfRangeException NotSupportedCommandType(CommandType value)
        {
#if DEBUG
            switch (value)
            {
                case CommandType.Text:
                case CommandType.StoredProcedure:
                    Debug.Fail("valid CommandType " + value.ToString());
                    break;
                case CommandType.TableDirect:
                    break;
                default:
                    Debug.Fail("invalid CommandType " + value.ToString());
                    break;
            }
#endif
            return NotSupportedEnumerationValue(typeof(CommandType), (int)value);
        }
        internal static ArgumentOutOfRangeException NotSupportedIsolationLevel(System.Data.IsolationLevel value)
        {
#if DEBUG
            switch (value)
            {
                case System.Data.IsolationLevel.Unspecified:
                case System.Data.IsolationLevel.ReadCommitted:
                case System.Data.IsolationLevel.ReadUncommitted:
                case System.Data.IsolationLevel.RepeatableRead:
                case System.Data.IsolationLevel.Serializable:
                case System.Data.IsolationLevel.Snapshot:
                    Debug.Fail("valid IsolationLevel " + value.ToString());
                    break;
                case System.Data.IsolationLevel.Chaos:
                    break;
                default:
                    Debug.Fail("invalid IsolationLevel " + value.ToString());
                    break;
            }
#endif
            return NotSupportedEnumerationValue(typeof(System.Data.IsolationLevel), (int)value);
        }

        internal static Exception OperationCancelled()
        {
            Exception exception = ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_OperationCancelled));
            return exception;
        }

        internal static Exception PendingBeginXXXExists()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_PendingBeginXXXExists));
        }

        internal static ArgumentOutOfRangeException InvalidSqlDependencyTimeout(string param)
        {
            return ADP.ArgumentOutOfRange(System.SRHelper.GetString(SR.SqlDependency_InvalidTimeout), param);
        }

        internal static Exception NonXmlResult()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_NonXmlResult));
        }

        //
        // SQL.DataParameter
        //
        internal static Exception InvalidUdt3PartNameFormat()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_InvalidUdt3PartNameFormat));
        }
        internal static Exception InvalidParameterTypeNameFormat()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_InvalidParameterTypeNameFormat));
        }
        internal static Exception InvalidParameterNameLength(string value)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_InvalidParameterNameLength, value));
        }
        internal static Exception PrecisionValueOutOfRange(byte precision)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_PrecisionValueOutOfRange, precision.ToString(CultureInfo.InvariantCulture)));
        }
        internal static Exception ScaleValueOutOfRange(byte scale)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_ScaleValueOutOfRange, scale.ToString(CultureInfo.InvariantCulture)));
        }
        internal static Exception TimeScaleValueOutOfRange(byte scale)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_TimeScaleValueOutOfRange, scale.ToString(CultureInfo.InvariantCulture)));
        }
        internal static Exception InvalidSqlDbType(SqlDbType value)
        {
            return ADP.InvalidEnumerationValue(typeof(SqlDbType), (int)value);
        }
        internal static Exception UnsupportedTVPOutputParameter(ParameterDirection direction, string paramName)
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SqlParameter_UnsupportedTVPOutputParameter,
                        direction.ToString(), paramName));
        }
        internal static Exception DBNullNotSupportedForTVPValues(string paramName)
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SqlParameter_DBNullNotSupportedForTVP, paramName));
        }
        internal static Exception UnexpectedTypeNameForNonStructParams(string paramName)
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SqlParameter_UnexpectedTypeNameForNonStruct, paramName));
        }
        internal static Exception ParameterInvalidVariant(string paramName)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParameterInvalidVariant, paramName));
        }

        internal static Exception MustSetTypeNameForParam(string paramType, string paramName)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_ParameterTypeNameRequired, paramType, paramName));
        }
        internal static Exception NullSchemaTableDataTypeNotSupported(string columnName)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.NullSchemaTableDataTypeNotSupported, columnName));
        }
        internal static Exception InvalidSchemaTableOrdinals()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.InvalidSchemaTableOrdinals));
        }
        internal static Exception EnumeratedRecordMetaDataChanged(string fieldName, int recordNumber)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_EnumeratedRecordMetaDataChanged, fieldName, recordNumber));
        }
        internal static Exception EnumeratedRecordFieldCountChanged(int recordNumber)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_EnumeratedRecordFieldCountChanged, recordNumber));
        }

        //
        // SQL.SqlDataAdapter
        //

        //
        // SQL.TDSParser
        //
        internal static Exception InvalidTDSVersion()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_InvalidTDSVersion));
        }
        internal static Exception ParsingError()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingError));
        }
        internal static Exception ParsingError(ParsingErrorState state)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingErrorWithState, ((int)state).ToString(CultureInfo.InvariantCulture)));
        }
        internal static Exception ParsingError(ParsingErrorState state, Exception innerException)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingErrorWithState, ((int)state).ToString(CultureInfo.InvariantCulture)), innerException);
        }
        internal static Exception ParsingErrorValue(ParsingErrorState state, int value)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingErrorValue, ((int)state).ToString(CultureInfo.InvariantCulture), value));
        }
        internal static Exception ParsingErrorFeatureId(ParsingErrorState state, int featureId)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingErrorFeatureId, ((int)state).ToString(CultureInfo.InvariantCulture), featureId));
        }
        internal static Exception ParsingErrorToken(ParsingErrorState state, int token)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingErrorToken, ((int)state).ToString(CultureInfo.InvariantCulture), token));
        }
        internal static Exception ParsingErrorLength(ParsingErrorState state, int length)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingErrorLength, ((int)state).ToString(CultureInfo.InvariantCulture), length));
        }
        internal static Exception ParsingErrorStatus(ParsingErrorState state, int status)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingErrorStatus, ((int)state).ToString(CultureInfo.InvariantCulture), status));
        }
        internal static Exception ParsingErrorOffset(ParsingErrorState state, int offset)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ParsingErrorOffset, ((int)state).ToString(CultureInfo.InvariantCulture), offset));
        }
        internal static Exception MoneyOverflow(string moneyValue)
        {
            return ADP.Overflow(System.SRHelper.GetString(SR.SQL_MoneyOverflow, moneyValue));
        }
        internal static Exception SmallDateTimeOverflow(string datetime)
        {
            return ADP.Overflow(System.SRHelper.GetString(SR.SQL_SmallDateTimeOverflow, datetime));
        }
        internal static Exception SNIPacketAllocationFailure()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_SNIPacketAllocationFailure));
        }
        internal static Exception TimeOverflow(string time)
        {
            return ADP.Overflow(System.SRHelper.GetString(SR.SQL_TimeOverflow, time));
        }

        //
        // SQL.SqlDataReader
        //
        internal static Exception InvalidRead()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_InvalidRead));
        }

        internal static Exception NonBlobColumn(string columnName)
        {
            return ADP.InvalidCast(System.SRHelper.GetString(SR.SQL_NonBlobColumn, columnName));
        }

        internal static Exception NonCharColumn(string columnName)
        {
            return ADP.InvalidCast(System.SRHelper.GetString(SR.SQL_NonCharColumn, columnName));
        }

        internal static Exception StreamNotSupportOnColumnType(string columnName)
        {
            return ADP.InvalidCast(System.SRHelper.GetString(SR.SQL_StreamNotSupportOnColumnType, columnName));
        }

        internal static Exception StreamNotSupportOnEncryptedColumn(string columnName)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_StreamNotSupportOnEncryptedColumn, columnName, "Stream"));
        }

        internal static Exception SequentialAccessNotSupportedOnEncryptedColumn(string columnName)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_SequentialAccessNotSupportedOnEncryptedColumn, columnName, "CommandBehavior=SequentialAccess"));
        }

        internal static Exception TextReaderNotSupportOnColumnType(string columnName)
        {
            return ADP.InvalidCast(System.SRHelper.GetString(SR.SQL_TextReaderNotSupportOnColumnType, columnName));
        }

        internal static Exception XmlReaderNotSupportOnColumnType(string columnName)
        {
            return ADP.InvalidCast(System.SRHelper.GetString(SR.SQL_XmlReaderNotSupportOnColumnType, columnName));
        }

        internal static Exception UDTUnexpectedResult(string exceptionText)
        {
            return ADP.TypeLoad(System.SRHelper.GetString(SR.SQLUDT_Unexpected, exceptionText));
        }

        //
        // SQL.SqlDependency
        //
        internal static Exception SqlCommandHasExistingSqlNotificationRequest()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQLNotify_AlreadyHasCommand));
        }

        internal static Exception SqlDepDefaultOptionsButNoStart()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlDependency_DefaultOptionsButNoStart));
        }

        internal static Exception SqlDependencyDatabaseBrokerDisabled()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlDependency_DatabaseBrokerDisabled));
        }

        internal static Exception SqlDependencyEventNoDuplicate()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlDependency_EventNoDuplicate));
        }

        internal static Exception SqlDependencyDuplicateStart()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlDependency_DuplicateStart));
        }

        internal static Exception SqlDependencyIdMismatch()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlDependency_IdMismatch));
        }

        internal static Exception SqlDependencyNoMatchingServerStart()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlDependency_NoMatchingServerStart));
        }

        internal static Exception SqlDependencyNoMatchingServerDatabaseStart()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlDependency_NoMatchingServerDatabaseStart));
        }

        //
        // SQL.SqlDelegatedTransaction
        //
        internal static TransactionPromotionException PromotionFailed(Exception inner)
        {
            TransactionPromotionException e = new TransactionPromotionException(System.SRHelper.GetString(SR.SqlDelegatedTransaction_PromotionFailed), inner);
            ADP.TraceExceptionAsReturnValue(e);
            return e;
        }
        //Failure while attempting to promote transaction.

        //
        // SQL.SqlMetaData
        //
        internal static Exception UnexpectedUdtTypeNameForNonUdtParams()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQLUDT_UnexpectedUdtTypeName));
        }
        internal static Exception MustSetUdtTypeNameForUdtParams()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQLUDT_InvalidUdtTypeName));
        }
        internal static Exception UDTInvalidSqlType(string typeName)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQLUDT_InvalidSqlType, typeName));
        }
        internal static Exception InvalidSqlDbTypeForConstructor(SqlDbType type)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SqlMetaData_InvalidSqlDbTypeForConstructorFormat, type.ToString()));
        }

        internal static Exception NameTooLong(string parameterName)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SqlMetaData_NameTooLong), parameterName);
        }

        internal static Exception InvalidSortOrder(SortOrder order)
        {
            return ADP.InvalidEnumerationValue(typeof(SortOrder), (int)order);
        }

        internal static Exception MustSpecifyBothSortOrderAndOrdinal(SortOrder order, int ordinal)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlMetaData_SpecifyBothSortOrderAndOrdinal, order.ToString(), ordinal));
        }

        internal static Exception UnsupportedColumnTypeForSqlProvider(string columnName, string typeName)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SqlProvider_InvalidDataColumnType, columnName, typeName));
        }
        internal static Exception InvalidColumnMaxLength(string columnName, long maxLength)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SqlProvider_InvalidDataColumnMaxLength, columnName, maxLength));
        }
        internal static Exception InvalidColumnPrecScale()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SqlMisc_InvalidPrecScaleMessage));
        }
        internal static Exception NotEnoughColumnsInStructuredType()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SqlProvider_NotEnoughColumnsInStructuredType));
        }
        internal static Exception DuplicateSortOrdinal(int sortOrdinal)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlProvider_DuplicateSortOrdinal, sortOrdinal));
        }
        internal static Exception MissingSortOrdinal(int sortOrdinal)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlProvider_MissingSortOrdinal, sortOrdinal));
        }
        internal static Exception SortOrdinalGreaterThanFieldCount(int columnOrdinal, int sortOrdinal)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlProvider_SortOrdinalGreaterThanFieldCount, sortOrdinal, columnOrdinal));
        }
        internal static Exception IEnumerableOfSqlDataRecordHasNoRows()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.IEnumerableOfSqlDataRecordHasNoRows));
        }




        //
        // SQL.BulkLoad
        //
        internal static Exception BulkLoadMappingInaccessible()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadMappingInaccessible));
        }
        internal static Exception BulkLoadMappingsNamesOrOrdinalsOnly()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadMappingsNamesOrOrdinalsOnly));
        }
        internal static Exception BulkLoadCannotConvertValue(Type sourcetype, MetaType metatype, Exception e)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadCannotConvertValue, sourcetype.Name, metatype.TypeName), e);
        }
        internal static Exception BulkLoadNonMatchingColumnMapping()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadNonMatchingColumnMapping));
        }
        internal static Exception BulkLoadNonMatchingColumnName(string columnName)
        {
            return BulkLoadNonMatchingColumnName(columnName, null);
        }
        internal static Exception BulkLoadNonMatchingColumnName(string columnName, Exception e)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadNonMatchingColumnName, columnName), e);
        }
        internal static Exception BulkLoadStringTooLong(string tableName, string columnName, string truncatedValue)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadStringTooLong, tableName, columnName, truncatedValue));
        }
        internal static Exception BulkLoadInvalidVariantValue()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadInvalidVariantValue));
        }
        internal static Exception BulkLoadInvalidTimeout(int timeout)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_BulkLoadInvalidTimeout, timeout.ToString(CultureInfo.InvariantCulture)));
        }
        internal static Exception BulkLoadExistingTransaction()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadExistingTransaction));
        }
        internal static Exception BulkLoadNoCollation()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadNoCollation));
        }
        internal static Exception BulkLoadConflictingTransactionOption()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQL_BulkLoadConflictingTransactionOption));
        }
        internal static Exception BulkLoadLcidMismatch(int sourceLcid, string sourceColumnName, int destinationLcid, string destinationColumnName)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.Sql_BulkLoadLcidMismatch, sourceLcid, sourceColumnName, destinationLcid, destinationColumnName));
        }
        internal static Exception InvalidOperationInsideEvent()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadInvalidOperationInsideEvent));
        }
        internal static Exception BulkLoadMissingDestinationTable()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadMissingDestinationTable));
        }
        internal static Exception BulkLoadInvalidDestinationTable(string tableName, Exception inner)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadInvalidDestinationTable, tableName), inner);
        }
        internal static Exception BulkLoadBulkLoadNotAllowDBNull(string columnName)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadNotAllowDBNull, columnName));
        }
        internal static Exception BulkLoadPendingOperation()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BulkLoadPendingOperation));
        }
        internal static Exception InvalidTableDerivedPrecisionForTvp(string columnName, byte precision)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlParameter_InvalidTableDerivedPrecisionForTvp, precision, columnName, System.Data.SqlTypes.SqlDecimal.MaxPrecision));
        }

        //
        // transactions.
        //
        internal static Exception ConnectionDoomed()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_ConnectionDoomed));
        }

        internal static Exception OpenResultCountExceeded()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_OpenResultCountExceeded));
        }

        internal static Exception UnsupportedSysTxForGlobalTransactions()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_UnsupportedSysTxVersion));
        }

        internal static readonly byte[] AttentionHeader = new byte[] {
            TdsEnums.MT_ATTN,               // Message Type
            TdsEnums.ST_EOM,                // Status
            TdsEnums.HEADER_LEN >> 8,       // length - upper byte
            TdsEnums.HEADER_LEN & 0xff,     // length - lower byte
            0,                              // spid
            0,                              // spid
            0,                              // packet (out of band)
            0                               // window
        };

        //
        // MultiSubnetFailover
        //

        /// <summary>
        /// used to block two scenarios if MultiSubnetFailover is true: 
        /// * server-provided failover partner - raising SqlException in this case
        /// * connection string with failover partner and MultiSubnetFailover=true - raising argument one in this case with the same message
        /// </summary>
        internal static Exception MultiSubnetFailoverWithFailoverPartner(bool serverProvidedFailoverPartner, SqlInternalConnectionTds internalConnection)
        {
            string msg = System.SRHelper.GetString(SR.SQLMSF_FailoverPartnerNotSupported);
            if (serverProvidedFailoverPartner)
            {
                // Replacing InvalidOperation with SQL exception
                SqlErrorCollection errors = new SqlErrorCollection();
                errors.Add(new SqlError(0, (byte)0x00, TdsEnums.FATAL_ERROR_CLASS, null, msg, "", 0));
                SqlException exc = SqlException.CreateException(errors, null, internalConnection);
                exc._doNotReconnect = true; // disable open retry logic on this error
                return exc;
            }
            else
            {
                return ADP.Argument(msg);
            }
        }

        internal static Exception MultiSubnetFailoverWithMoreThan64IPs()
        {
            string msg = GetSNIErrorMessage((int)SNINativeMethodWrapper.SniSpecialErrors.MultiSubnetFailoverWithMoreThan64IPs);
            return ADP.InvalidOperation(msg);
        }

        internal static Exception MultiSubnetFailoverWithInstanceSpecified()
        {
            string msg = GetSNIErrorMessage((int)SNINativeMethodWrapper.SniSpecialErrors.MultiSubnetFailoverWithInstanceSpecified);
            return ADP.Argument(msg);
        }

        internal static Exception MultiSubnetFailoverWithNonTcpProtocol()
        {
            string msg = GetSNIErrorMessage((int)SNINativeMethodWrapper.SniSpecialErrors.MultiSubnetFailoverWithNonTcpProtocol);
            return ADP.Argument(msg);
        }

        //
        // Read-only routing
        //

        internal static Exception ROR_FailoverNotSupportedConnString()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.SQLROR_FailoverNotSupported));
        }

        internal static Exception ROR_FailoverNotSupportedServer(SqlInternalConnectionTds internalConnection)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, (byte)0x00, TdsEnums.FATAL_ERROR_CLASS, null, (System.SRHelper.GetString(SR.SQLROR_FailoverNotSupported)), "", 0));
            SqlException exc = SqlException.CreateException(errors, null, internalConnection);
            exc._doNotReconnect = true;
            return exc;
        }

        internal static Exception ROR_RecursiveRoutingNotSupported(SqlInternalConnectionTds internalConnection)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, (byte)0x00, TdsEnums.FATAL_ERROR_CLASS, null, (System.SRHelper.GetString(SR.SQLROR_RecursiveRoutingNotSupported)), "", 0));
            SqlException exc = SqlException.CreateException(errors, null, internalConnection);
            exc._doNotReconnect = true;
            return exc;
        }

        internal static Exception ROR_UnexpectedRoutingInfo(SqlInternalConnectionTds internalConnection)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, (byte)0x00, TdsEnums.FATAL_ERROR_CLASS, null, (System.SRHelper.GetString(SR.SQLROR_UnexpectedRoutingInfo)), "", 0));
            SqlException exc = SqlException.CreateException(errors, null, internalConnection);
            exc._doNotReconnect = true;
            return exc;
        }

        internal static Exception ROR_InvalidRoutingInfo(SqlInternalConnectionTds internalConnection)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, (byte)0x00, TdsEnums.FATAL_ERROR_CLASS, null, (System.SRHelper.GetString(SR.SQLROR_InvalidRoutingInfo)), "", 0));
            SqlException exc = SqlException.CreateException(errors, null, internalConnection);
            exc._doNotReconnect = true;
            return exc;
        }

        internal static Exception ROR_TimeoutAfterRoutingInfo(SqlInternalConnectionTds internalConnection)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, (byte)0x00, TdsEnums.FATAL_ERROR_CLASS, null, (System.SRHelper.GetString(SR.SQLROR_TimeoutAfterRoutingInfo)), "", 0));
            SqlException exc = SqlException.CreateException(errors, null, internalConnection);
            exc._doNotReconnect = true;
            return exc;
        }

        //
        // Connection resiliency
        //
        internal static SqlException CR_ReconnectTimeout()
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(TdsEnums.TIMEOUT_EXPIRED, (byte)0x00, TdsEnums.MIN_ERROR_CLASS, null, SQLMessage.Timeout(), "", 0, TdsEnums.SNI_WAIT_TIMEOUT));
            SqlException exc = SqlException.CreateException(errors, "");
            return exc;
        }

        internal static SqlException CR_ReconnectionCancelled()
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, 0, TdsEnums.MIN_ERROR_CLASS, null, SQLMessage.OperationCancelled(), "", 0));
            SqlException exc = SqlException.CreateException(errors, "");
            return exc;
        }

        internal static Exception CR_NextAttemptWillExceedQueryTimeout(SqlException innerException, Guid connectionId)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, 0, TdsEnums.MIN_ERROR_CLASS, null, System.SRHelper.GetString(SR.SQLCR_NextAttemptWillExceedQueryTimeout), "", 0));
            SqlException exc = SqlException.CreateException(errors, "", connectionId, innerException);
            return exc;
        }

        internal static Exception CR_EncryptionChanged(SqlInternalConnectionTds internalConnection)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, 0, TdsEnums.FATAL_ERROR_CLASS, null, System.SRHelper.GetString(SR.SQLCR_EncryptionChanged), "", 0));
            SqlException exc = SqlException.CreateException(errors, "", internalConnection);
            return exc;
        }

        internal static SqlException CR_AllAttemptsFailed(SqlException innerException, Guid connectionId)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, 0, TdsEnums.MIN_ERROR_CLASS, null, System.SRHelper.GetString(SR.SQLCR_AllAttemptsFailed), "", 0));
            SqlException exc = SqlException.CreateException(errors, "", connectionId, innerException);
            return exc;
        }

        internal static SqlException CR_NoCRAckAtReconnection(SqlInternalConnectionTds internalConnection)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, 0, TdsEnums.FATAL_ERROR_CLASS, null, System.SRHelper.GetString(SR.SQLCR_NoCRAckAtReconnection), "", 0));
            SqlException exc = SqlException.CreateException(errors, "", internalConnection);
            return exc;
        }

        internal static SqlException CR_TDSVersionNotPreserved(SqlInternalConnectionTds internalConnection)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, 0, TdsEnums.FATAL_ERROR_CLASS, null, System.SRHelper.GetString(SR.SQLCR_TDSVestionNotPreserved), "", 0));
            SqlException exc = SqlException.CreateException(errors, "", internalConnection);
            return exc;
        }

        internal static SqlException CR_UnrecoverableServer(Guid connectionId)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, 0, TdsEnums.FATAL_ERROR_CLASS, null, System.SRHelper.GetString(SR.SQLCR_UnrecoverableServer), "", 0));
            SqlException exc = SqlException.CreateException(errors, "", connectionId);
            return exc;
        }

        internal static SqlException CR_UnrecoverableClient(Guid connectionId)
        {
            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, 0, TdsEnums.FATAL_ERROR_CLASS, null, System.SRHelper.GetString(SR.SQLCR_UnrecoverableClient), "", 0));
            SqlException exc = SqlException.CreateException(errors, "", connectionId);
            return exc;
        }

        internal static Exception StreamWriteNotSupported()
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_StreamWriteNotSupported));
        }
        internal static Exception StreamReadNotSupported()
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_StreamReadNotSupported));
        }
        internal static Exception StreamSeekNotSupported()
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_StreamSeekNotSupported));
        }
        internal static System.Data.SqlTypes.SqlNullValueException SqlNullValue()
        {
            System.Data.SqlTypes.SqlNullValueException e = new System.Data.SqlTypes.SqlNullValueException();
            return e;
        }
        internal static Exception SubclassMustOverride()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SqlMisc_SubclassMustOverride));
        }

        // ProjectK\CoreCLR specific errors
        internal static Exception UnsupportedKeyword(string keyword)
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_UnsupportedKeyword, keyword));
        }
        internal static Exception NetworkLibraryKeywordNotSupported()
        {
            return ADP.NotSupported(System.SRHelper.GetString(SR.SQL_NetworkLibraryNotSupported));
        }
        internal static Exception UnsupportedFeatureAndToken(SqlInternalConnectionTds internalConnection, string token)
        {
            var innerException = ADP.NotSupported(System.SRHelper.GetString(SR.SQL_UnsupportedToken, token));

            SqlErrorCollection errors = new SqlErrorCollection();
            errors.Add(new SqlError(0, 0, TdsEnums.FATAL_ERROR_CLASS, null, System.SRHelper.GetString(SR.SQL_UnsupportedFeature), "", 0));
            SqlException exc = SqlException.CreateException(errors, "", internalConnection, innerException);
            return exc;
        }

        internal static Exception BatchedUpdatesNotAvailableOnContextConnection()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.SQL_BatchedUpdatesNotAvailableOnContextConnection));
        }

        #region Always Encrypted Errors

        #region Always Encrypted - Certificate Store Provider Errors
        internal static Exception InvalidKeyEncryptionAlgorithm(string encryptionAlgorithm, string validEncryptionAlgorithm, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_InvalidKeyEncryptionAlgorithmSysErr : SR.TCE_InvalidKeyEncryptionAlgorithm;
            return ADP.Argument(System.SRHelper.GetString(message, encryptionAlgorithm, validEncryptionAlgorithm), TdsEnums.TCE_PARAM_ENCRYPTION_ALGORITHM);
        }

        internal static Exception NullKeyEncryptionAlgorithm(bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_NullKeyEncryptionAlgorithmSysErr : SR.TCE_NullKeyEncryptionAlgorithm;
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_ENCRYPTION_ALGORITHM, System.SRHelper.GetString(message));
        }

        internal static Exception EmptyColumnEncryptionKey()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_EmptyColumnEncryptionKey), TdsEnums.TCE_PARAM_COLUMNENCRYPTION_KEY);
        }

        internal static Exception NullColumnEncryptionKey()
        {
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_COLUMNENCRYPTION_KEY, System.SRHelper.GetString(SR.TCE_NullColumnEncryptionKey));
        }

        internal static Exception EmptyEncryptedColumnEncryptionKey()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_EmptyEncryptedColumnEncryptionKey), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception NullEncryptedColumnEncryptionKey()
        {
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_ENCRYPTED_CEK, System.SRHelper.GetString(SR.TCE_NullEncryptedColumnEncryptionKey));
        }

        internal static Exception LargeCertificatePathLength(int actualLength, int maxLength, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_LargeCertificatePathLengthSysErr : SR.TCE_LargeCertificatePathLength;
            return ADP.Argument(System.SRHelper.GetString(message, actualLength, maxLength), TdsEnums.TCE_PARAM_MASTERKEY_PATH);

        }

        internal static Exception NullCertificatePath(string[] validLocations, bool isSystemOp)
        {
            Debug.Assert(2 == validLocations.Length);
            string message = isSystemOp ? SR.TCE_NullCertificatePathSysErr : SR.TCE_NullCertificatePath;
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_MASTERKEY_PATH, System.SRHelper.GetString(message, validLocations[0], validLocations[1], @"/"));
        }

        internal static Exception NullCspKeyPath(bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_NullCspPathSysErr : SR.TCE_NullCspPath;
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_MASTERKEY_PATH, System.SRHelper.GetString(message, @"/"));
        }

        internal static Exception NullCngKeyPath(bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_NullCngPathSysErr : SR.TCE_NullCngPath;
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_MASTERKEY_PATH, System.SRHelper.GetString(message, @"/"));
        }

        internal static Exception InvalidCertificatePath(string actualCertificatePath, string[] validLocations, bool isSystemOp)
        {
            Debug.Assert(2 == validLocations.Length);
            string message = isSystemOp ? SR.TCE_InvalidCertificatePathSysErr : SR.TCE_InvalidCertificatePath;
            return ADP.Argument(System.SRHelper.GetString(message, actualCertificatePath, validLocations[0], validLocations[1], @"/"), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception InvalidCspPath(string masterKeyPath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_InvalidCspPathSysErr : SR.TCE_InvalidCspPath;
            return ADP.Argument(System.SRHelper.GetString(message, masterKeyPath, @"/"), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception InvalidCngPath(string masterKeyPath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_InvalidCngPathSysErr : SR.TCE_InvalidCngPath;
            return ADP.Argument(System.SRHelper.GetString(message, masterKeyPath, @"/"), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception EmptyCspName(string masterKeyPath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_EmptyCspNameSysErr : SR.TCE_EmptyCspName;
            return ADP.Argument(System.SRHelper.GetString(message, masterKeyPath, @"/"), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception EmptyCngName(string masterKeyPath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_EmptyCngNameSysErr : SR.TCE_EmptyCngName;
            return ADP.Argument(System.SRHelper.GetString(message, masterKeyPath, @"/"), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception EmptyCspKeyId(string masterKeyPath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_EmptyCspKeyIdSysErr : SR.TCE_EmptyCspKeyId;
            return ADP.Argument(System.SRHelper.GetString(message, masterKeyPath, @"/"), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception EmptyCngKeyId(string masterKeyPath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_EmptyCngKeyIdSysErr : SR.TCE_EmptyCngKeyId;
            return ADP.Argument(System.SRHelper.GetString(message, masterKeyPath, @"/"), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception InvalidCspName(string cspName, string masterKeyPath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_InvalidCspNameSysErr : SR.TCE_InvalidCspName;
            return ADP.Argument(System.SRHelper.GetString(message, cspName, masterKeyPath), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception InvalidCspKeyIdentifier(string keyIdentifier, string masterKeyPath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_InvalidCspKeyIdSysErr : SR.TCE_InvalidCspKeyId;
            return ADP.Argument(System.SRHelper.GetString(message, keyIdentifier, masterKeyPath), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception InvalidCngKey(string masterKeyPath, string cngProviderName, string keyIdentifier, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_InvalidCngKeySysErr : SR.TCE_InvalidCngKey;
            return ADP.Argument(System.SRHelper.GetString(message, masterKeyPath, cngProviderName, keyIdentifier), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception InvalidCertificateLocation(string certificateLocation, string certificatePath, string[] validLocations, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_InvalidCertificateLocationSysErr : SR.TCE_InvalidCertificateLocation;
            return ADP.Argument(System.SRHelper.GetString(message, certificateLocation, certificatePath, validLocations[0], validLocations[1], @"/"), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception InvalidCertificateStore(string certificateStore, string certificatePath, string validCertificateStore, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_InvalidCertificateStoreSysErr : SR.TCE_InvalidCertificateStore;
            return ADP.Argument(System.SRHelper.GetString(message, certificateStore, certificatePath, validCertificateStore), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception EmptyCertificateThumbprint(string certificatePath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_EmptyCertificateThumbprintSysErr : SR.TCE_EmptyCertificateThumbprint;
            return ADP.Argument(System.SRHelper.GetString(message, certificatePath), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception CertificateNotFound(string thumbprint, string certificateLocation, string certificateStore, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_CertificateNotFoundSysErr : SR.TCE_CertificateNotFound;
            return ADP.Argument(System.SRHelper.GetString(message, thumbprint, certificateLocation, certificateStore), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }

        internal static Exception InvalidAlgorithmVersionInEncryptedCEK(byte actual, byte expected)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidAlgorithmVersionInEncryptedCEK, actual.ToString(@"X2"), expected.ToString(@"X2")), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception InvalidCiphertextLengthInEncryptedCEK(int actual, int expected, string certificateName)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidCiphertextLengthInEncryptedCEK, actual, expected, certificateName), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception InvalidCiphertextLengthInEncryptedCEKCsp(int actual, int expected, string masterKeyPath)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidCiphertextLengthInEncryptedCEKCsp, actual, expected, masterKeyPath), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception InvalidCiphertextLengthInEncryptedCEKCng(int actual, int expected, string masterKeyPath)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidCiphertextLengthInEncryptedCEKCng, actual, expected, masterKeyPath), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception InvalidSignatureInEncryptedCEK(int actual, int expected, string masterKeyPath)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidSignatureInEncryptedCEK, actual, expected, masterKeyPath), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception InvalidSignatureInEncryptedCEKCsp(int actual, int expected, string masterKeyPath)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidSignatureInEncryptedCEKCsp, actual, expected, masterKeyPath), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception InvalidSignatureInEncryptedCEKCng(int actual, int expected, string masterKeyPath)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidSignatureInEncryptedCEKCng, actual, expected, masterKeyPath), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception InvalidCertificateSignature(string certificatePath)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidCertificateSignature, certificatePath), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception InvalidSignature(string masterKeyPath)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidSignature, masterKeyPath), TdsEnums.TCE_PARAM_ENCRYPTED_CEK);
        }

        internal static Exception CertificateWithNoPrivateKey(string keyPath, bool isSystemOp)
        {
            string message = isSystemOp ? SR.TCE_CertificateWithNoPrivateKeySysErr : SR.TCE_CertificateWithNoPrivateKey;
            return ADP.Argument(System.SRHelper.GetString(message, keyPath), TdsEnums.TCE_PARAM_MASTERKEY_PATH);
        }
        #endregion Always Encrypted - Certificate Store Provider Errors

        #region Always Encrypted - Cryptographic Algorithms Error messages
        internal static Exception NullPlainText()
        {
            return ADP.ArgumentNull(System.SRHelper.GetString(SR.TCE_NullPlainText));
        }

        internal static Exception NullCipherText()
        {
            return ADP.ArgumentNull(System.SRHelper.GetString(SR.TCE_NullCipherText));
        }

        internal static Exception NullColumnEncryptionAlgorithm(string supportedAlgorithms)
        {
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_ENCRYPTION_ALGORITHM, System.SRHelper.GetString(SR.TCE_NullColumnEncryptionAlgorithm, supportedAlgorithms));
        }

        internal static Exception NullColumnEncryptionKeySysErr()
        {
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_ENCRYPTIONKEY, System.SRHelper.GetString(SR.TCE_NullColumnEncryptionKeySysErr));
        }

        internal static Exception InvalidKeySize(string algorithmName, int actualKeylength, int expectedLength)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidKeySize, algorithmName, actualKeylength, expectedLength), TdsEnums.TCE_PARAM_ENCRYPTIONKEY);
        }

        internal static Exception InvalidEncryptionType(string algorithmName, SqlClientEncryptionType encryptionType, params SqlClientEncryptionType[] validEncryptionTypes)
        {
            const string valueSeparator = @", ";
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidEncryptionType, algorithmName, encryptionType.ToString(), string.Join(valueSeparator, validEncryptionTypes.Select((validEncryptionType => @"'" + validEncryptionType + @"'")))), TdsEnums.TCE_PARAM_ENCRYPTIONTYPE);
        }

        internal static Exception InvalidCipherTextSize(int actualSize, int minimumSize)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidCipherTextSize, actualSize, minimumSize), TdsEnums.TCE_PARAM_CIPHERTEXT);
        }

        internal static Exception InvalidAlgorithmVersion(byte actual, byte expected)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidAlgorithmVersion, actual.ToString(@"X2"), expected.ToString(@"X2")), TdsEnums.TCE_PARAM_CIPHERTEXT);
        }

        internal static Exception InvalidAuthenticationTag()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidAuthenticationTag), TdsEnums.TCE_PARAM_CIPHERTEXT);
        }
        #endregion Always Encrypted - Cryptographic Algorithms Error messages

        #region Always Encrypted - Errors from sp_describe_parameter_encryption
        internal static Exception UnexpectedDescribeParamFormatParameterMetadata()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_UnexpectedDescribeParamFormatParameterMetadata, "sp_describe_parameter_encryption"));
        }

        internal static Exception UnexpectedDescribeParamFormatAttestationInfo(string enclaveType)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_UnexpectedDescribeParamFormatAttestationInfo, "sp_describe_parameter_encryption", enclaveType));
        }

        internal static Exception InvalidEncryptionKeyOrdinalEnclaveMetadata(int ordinal, int maxOrdinal)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_InvalidEncryptionKeyOrdinalEnclaveMetadata, ordinal, maxOrdinal));
        }

        internal static Exception InvalidEncryptionKeyOrdinalParameterMetadata(int ordinal, int maxOrdinal)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_InvalidEncryptionKeyOrdinalParameterMetadata, ordinal, maxOrdinal));
        }

        public static Exception MultipleRowsReturnedForAttestationInfo()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_MultipleRowsReturnedForAttestationInfo, "sp_describe_parameter_encryption"));
        }

        internal static Exception ParamEncryptionMetadataMissing(string paramName, string procedureName)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_ParamEncryptionMetaDataMissing, "sp_describe_parameter_encryption", paramName, procedureName));
        }

        internal static Exception ProcEncryptionMetadataMissing(string procedureName)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_ProcEncryptionMetaDataMissing, "sp_describe_parameter_encryption", procedureName));
        }

        internal static Exception UnableToVerifyColumnMasterKeySignature(Exception innerExeption)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_UnableToVerifyColumnMasterKeySignature, innerExeption.Message), innerExeption);
        }

        internal static Exception ColumnMasterKeySignatureVerificationFailed(string cmkPath)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_ColumnMasterKeySignatureVerificationFailed, cmkPath));
        }

        internal static Exception InvalidKeyStoreProviderName(string providerName, List<string> systemProviders, List<string> customProviders)
        {
            const string valueSeparator = @", ";
            string systemProviderStr = string.Join(valueSeparator, systemProviders.Select(provider => $"'{provider}'"));
            string customProviderStr = string.Join(valueSeparator, customProviders.Select(provider => $"'{provider}'"));
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidKeyStoreProviderName, providerName, systemProviderStr, customProviderStr));
        }

        internal static Exception ParamInvalidForceColumnEncryptionSetting(string paramName, string procedureName)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_ParamInvalidForceColumnEncryptionSetting, TdsEnums.TCE_PARAM_FORCE_COLUMN_ENCRYPTION, paramName, procedureName, "SqlParameter"));
        }

        internal static Exception ParamUnExpectedEncryptionMetadata(string paramName, string procedureName)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_ParamUnExpectedEncryptionMetadata, paramName, procedureName, TdsEnums.TCE_PARAM_FORCE_COLUMN_ENCRYPTION, "SqlParameter"));
        }

        internal static Exception ColumnMasterKeySignatureNotFound(string cmkPath)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_ColumnMasterKeySignatureNotFound, cmkPath));
        }
        #endregion Always Encrypted - Errors from sp_describe_parameter_encryption

        #region Always Encrypted - Errors from secure channel Communication

        internal static Exception ExceptionWhenGeneratingEnclavePackage(Exception innerExeption)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_ExceptionWhenGeneratingEnclavePackage, innerExeption.Message), innerExeption);
        }

        internal static Exception FailedToEncryptRegisterRulesBytePackage(Exception innerExeption)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_FailedToEncryptRegisterRulesBytePackage, innerExeption.Message), innerExeption);
        }

        internal static Exception InvalidKeyIdUnableToCastToUnsignedShort(int keyId, Exception innerException)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidKeyIdUnableToCastToUnsignedShort, keyId, innerException.Message), innerException);
        }

        internal static Exception InvalidDatabaseIdUnableToCastToUnsignedInt(int databaseId, Exception innerException)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidDatabaseIdUnableToCastToUnsignedInt, databaseId, innerException.Message), innerException);
        }

        internal static Exception InvalidAttestationParameterUnableToConvertToUnsignedInt(string variableName, int intValue, string enclaveType, Exception innerException)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidAttestationParameterUnableToConvertToUnsignedInt, enclaveType, intValue, variableName, innerException.Message), innerException);
        }

        internal static Exception OffsetOutOfBounds(string argument, string type, string method)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_OffsetOutOfBounds, type, method));
        }

        internal static Exception InsufficientBuffer(string argument, string type, string method)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InsufficientBuffer, argument, type, method));
        }

        internal static Exception ColumnEncryptionKeysNotFound()
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_ColumnEncryptionKeysNotFound));
        }

        #endregion Always Encrypted - Errors from secure channel Communication

        #region Always Encrypted - Errors when performing attestation
        internal static Exception AttestationInfoNotReturnedFromSqlServer(string enclaveType, string enclaveAttestationUrl)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_AttestationInfoNotReturnedFromSQLServer, enclaveType, enclaveAttestationUrl));
        }
        #endregion Always Encrypted - Errors when performing attestation

        #region Always Encrypted - Errors when establishing secure channel
        internal static Exception NullArgumentInConstructorInternal(string argumentName, string objectUnderConstruction)
        {
            return ADP.ArgumentNull(argumentName, System.SRHelper.GetString(SR.TCE_NullArgumentInConstructorInternal, argumentName, objectUnderConstruction));
        }

        internal static Exception EmptyArgumentInConstructorInternal(string argumentName, string objectUnderConstruction)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_EmptyArgumentInConstructorInternal, argumentName, objectUnderConstruction));
        }

        internal static Exception NullArgumentInternal(string argumentName, string type, string method)
        {
            return ADP.ArgumentNull(argumentName, System.SRHelper.GetString(SR.TCE_NullArgumentInternal, argumentName, type, method));
        }

        internal static Exception EmptyArgumentInternal(string argumentName, string type, string method)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_EmptyArgumentInternal, argumentName, type, method));
        }
        #endregion Always Encrypted - Errors when establishing secure channel

        #region Always Encrypted - Enclave provider/configuration errors

        internal static Exception CannotGetSqlColumnEncryptionEnclaveProviderConfig(Exception innerException)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_CannotGetSqlColumnEncryptionEnclaveProviderConfig, innerException.Message), innerException);
        }

        internal static Exception CannotCreateSqlColumnEncryptionEnclaveProvider(string providerName, string type, Exception innerException)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_CannotCreateSqlColumnEncryptionEnclaveProvider, providerName, type, innerException.Message), innerException);
        }

        internal static Exception SqlColumnEncryptionEnclaveProviderNameCannotBeEmpty()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_SqlColumnEncryptionEnclaveProviderNameCannotBeEmpty));
        }

        internal static Exception NoAttestationUrlSpecifiedForEnclaveBasedQuerySpDescribe(string enclaveType)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_NoAttestationUrlSpecifiedForEnclaveBasedQuerySpDescribe, "sp_describe_parameter_encryption", enclaveType));
        }

        internal static Exception NoAttestationUrlSpecifiedForEnclaveBasedQueryGeneratingEnclavePackage(string enclaveType)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_NoAttestationUrlSpecifiedForEnclaveBasedQueryGeneratingEnclavePackage, enclaveType));
        }

        internal static Exception EnclaveTypeNullForEnclaveBasedQuery()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_EnclaveTypeNullForEnclaveBasedQuery));
        }

        internal static Exception EnclaveProvidersNotConfiguredForEnclaveBasedQuery()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_EnclaveProvidersNotConfiguredForEnclaveBasedQuery));
        }

        internal static Exception EnclaveProviderNotFound(string enclaveType)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_EnclaveProviderNotFound, enclaveType));
        }

        internal static Exception NullEnclaveSessionReturnedFromProvider(string enclaveType, string attestationUrl)
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_NullEnclaveSessionReturnedFromProvider, enclaveType, attestationUrl));
        }

        #endregion Always Encrypted - Enclave provider/configuration errors

        #region Always Encrypted - Generic toplevel failures

        internal static Exception GetExceptionArray(string serverName, string errorMessage, Exception e)
        {
            // Create and throw an exception array
            SqlErrorCollection sqlErs = new SqlErrorCollection();
            Exception exceptionToInclude = (null != e.InnerException) ? e.InnerException : e;
            sqlErs.Add(new SqlError(infoNumber: 0, errorState: (byte)0x00, errorClass: (byte)TdsEnums.MIN_ERROR_CLASS, server: serverName, errorMessage: errorMessage, procedure: null, lineNumber: 0));

            if (e is SqlException)
            {
                SqlException exThrown = (SqlException)e;
                SqlErrorCollection errorList = exThrown.Errors;
                for (int i = 0; i < exThrown.Errors.Count; i++)
                {
                    sqlErs.Add(errorList[i]);
                }
            }
            else
            {
                sqlErs.Add(new SqlError(infoNumber: 0, errorState: (byte)0x00, errorClass: (byte)TdsEnums.MIN_ERROR_CLASS, server: serverName, errorMessage: e.Message, procedure: null, lineNumber: 0));
            }

            return SqlException.CreateException(sqlErs, "", null, exceptionToInclude);
        }

        internal static Exception ColumnDecryptionFailed(string columnName, string serverName, Exception e)
        {
            return GetExceptionArray(serverName, System.SRHelper.GetString(SR.TCE_ColumnDecryptionFailed, columnName), e);
        }

        internal static Exception ParamEncryptionFailed(string paramName, string serverName, Exception e)
        {
            return GetExceptionArray(serverName, System.SRHelper.GetString(SR.TCE_ParamEncryptionFailed, paramName), e);
        }

        internal static Exception ParamDecryptionFailed(string paramName, string serverName, Exception e)
        {
            return GetExceptionArray(serverName, System.SRHelper.GetString(SR.TCE_ParamDecryptionFailed, paramName), e);
        }
        #endregion Always Encrypted - Generic toplevel failures

        #region Always Encrypted - Client side query processing errors

        internal static Exception UnknownColumnEncryptionAlgorithm(string algorithmName, string supportedAlgorithms)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_UnknownColumnEncryptionAlgorithm, algorithmName, supportedAlgorithms));
        }

        internal static Exception UnknownColumnEncryptionAlgorithmId(int algoId, string supportAlgorithmIds)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_UnknownColumnEncryptionAlgorithmId, algoId, supportAlgorithmIds), TdsEnums.TCE_PARAM_CIPHER_ALGORITHM_ID);
        }

        internal static Exception UnsupportedNormalizationVersion(byte version)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_UnsupportedNormalizationVersion, version, "'1'", "SQL Server"));
        }

        internal static Exception UnrecognizedKeyStoreProviderName(string providerName, List<string> systemProviders, List<string> customProviders)
        {
            const string valueSeparator = @", ";
            string systemProviderStr = string.Join(valueSeparator, systemProviders.Select(provider => @"'" + provider + @"'"));
            string customProviderStr = string.Join(valueSeparator, customProviders.Select(provider => @"'" + provider + @"'"));
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_UnrecognizedKeyStoreProviderName, providerName, systemProviderStr, customProviderStr));
        }

        internal static Exception InvalidDataTypeForEncryptedParameter(string parameterName, int actualDataType, int expectedDataType)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_NullProviderValue, parameterName, actualDataType, expectedDataType));
        }

        internal static Exception KeyDecryptionFailed(string providerName, string keyHex, Exception e)
        {

            if (providerName.Equals(SqlColumnEncryptionCertificateStoreProvider.ProviderName))
            {
                return GetExceptionArray(null, System.SRHelper.GetString(SR.TCE_KeyDecryptionFailedCertStore, providerName, keyHex), e);
            }
            else
            {
                return GetExceptionArray(null, System.SRHelper.GetString(SR.TCE_KeyDecryptionFailed, providerName, keyHex), e);
            }
        }

        internal static Exception UntrustedKeyPath(string keyPath, string serverName)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_UntrustedKeyPath, keyPath, serverName));
        }

        internal static Exception UnsupportedDatatypeEncryption(string dataType)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_UnsupportedDatatype, dataType));
        }

        internal static Exception ThrowDecryptionFailed(string keyStr, string valStr, Exception e)
        {
            return GetExceptionArray(null, System.SRHelper.GetString(SR.TCE_DecryptionFailed, keyStr, valStr), e);
        }

        internal static Exception NullEnclaveSessionDuringQueryExecution(string enclaveType, string enclaveAttestationUrl)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_NullEnclaveSessionDuringQueryExecution, enclaveType, enclaveAttestationUrl));
        }

        internal static Exception NullEnclavePackageForEnclaveBasedQuery(string enclaveType, string enclaveAttestationUrl)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_NullEnclavePackageForEnclaveBasedQuery, enclaveType, enclaveAttestationUrl));
        }

        #endregion Always Encrypted - Client side query processing errors

        #region Always Encrypted - SQL connection related error messages

        internal static Exception TceNotSupported()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_NotSupportedByServer, "SQL Server"));
        }

        internal static Exception EnclaveComputationsNotSupported()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_EnclaveComputationsNotSupported));
        }

        internal static Exception EnclaveTypeNotReturned()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_EnclaveTypeNotReturned));
        }
        #endregion Always Encrypted - SQL connection related error messages

        #region Always Encrypted - Extensibility related error messages

        internal static Exception CanOnlyCallOnce()
        {
            return ADP.InvalidOperation(System.SRHelper.GetString(SR.TCE_CanOnlyCallOnce));
        }

        internal static Exception NullCustomKeyStoreProviderDictionary()
        {
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_CLIENT_KEYSTORE_PROVIDERS, System.SRHelper.GetString(SR.TCE_NullCustomKeyStoreProviderDictionary));
        }

        internal static Exception InvalidCustomKeyStoreProviderName(string providerName, string prefix)
        {
            return ADP.Argument(System.SRHelper.GetString(SR.TCE_InvalidCustomKeyStoreProviderName, providerName, prefix), TdsEnums.TCE_PARAM_CLIENT_KEYSTORE_PROVIDERS);
        }

        internal static Exception NullProviderValue(string providerName)
        {
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_CLIENT_KEYSTORE_PROVIDERS, System.SRHelper.GetString(SR.TCE_NullProviderValue, providerName));
        }

        internal static Exception EmptyProviderName()
        {
            return ADP.ArgumentNull(TdsEnums.TCE_PARAM_CLIENT_KEYSTORE_PROVIDERS, System.SRHelper.GetString(SR.TCE_EmptyProviderName));
        }
        #endregion Always Encrypted - Extensibility related error messages

        #endregion Always Encrypted Errors

        /// <summary>
        /// gets a message for SNI error (sniError must be valid, non-zero error code)
        /// </summary>
        internal static string GetSNIErrorMessage(int sniError)
        {
            Debug.Assert(sniError > 0 && sniError <= (int)SNINativeMethodWrapper.SniSpecialErrors.MaxErrorValue, "SNI error is out of range");

            string errorMessageId = string.Format("SNI_ERROR_{0}", sniError);
            return System.SRHelper.GetResourceString(errorMessageId);
        }

        // Default values for SqlDependency and SqlNotificationRequest
        internal const int SqlDependencyTimeoutDefault = 0;
        internal const int SqlDependencyServerTimeout = 5 * 24 * 3600; // 5 days - used to compute default TTL of the dependency
        internal const string SqlNotificationServiceDefault = "SqlQueryNotificationService";
        internal const string SqlNotificationStoredProcedureDefault = "SqlQueryNotificationStoredProcedure";
    }

    sealed internal class SQLMessage
    {
        private SQLMessage() { /* prevent utility class from being instantiated*/ }

        // The class SQLMessage defines the error messages that are specific to the SqlDataAdapter
        // that are caused by a netlib error.  The functions will be called and then return the
        // appropriate error message from the resource Framework.txt.  The SqlDataAdapter will then
        // take the error message and then create a SqlError for the message and then place
        // that into a SqlException that is either thrown to the user or cached for throwing at
        // a later time.  This class is used so that there will be compile time checking of error
        // messages.  The resource Framework.txt will ensure proper string text based on the appropriate
        // locale.

        internal static string CultureIdError()
        {
            return System.SRHelper.GetString(SR.SQL_CultureIdError);
        }
        internal static string EncryptionNotSupportedByClient()
        {
            return System.SRHelper.GetString(SR.SQL_EncryptionNotSupportedByClient);
        }
        internal static string EncryptionNotSupportedByServer()
        {
            return System.SRHelper.GetString(SR.SQL_EncryptionNotSupportedByServer);
        }
        internal static string OperationCancelled()
        {
            return System.SRHelper.GetString(SR.SQL_OperationCancelled);
        }
        internal static string SevereError()
        {
            return System.SRHelper.GetString(SR.SQL_SevereError);
        }
        internal static string SSPIInitializeError()
        {
            return System.SRHelper.GetString(SR.SQL_SSPIInitializeError);
        }
        internal static string SSPIGenerateError()
        {
            return System.SRHelper.GetString(SR.SQL_SSPIGenerateError);
        }
        internal static string SqlServerBrowserNotAccessible()
        {
            return System.SRHelper.GetString(SR.SQL_SqlServerBrowserNotAccessible);
        }
        internal static string KerberosTicketMissingError()
        {
            return System.SRHelper.GetString(SR.SQL_KerberosTicketMissingError);
        }
        internal static string Timeout()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_Execution);
        }
        internal static string Timeout_PreLogin_Begin()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_PreLogin_Begin);
        }
        internal static string Timeout_PreLogin_InitializeConnection()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_PreLogin_InitializeConnection);
        }
        internal static string Timeout_PreLogin_SendHandshake()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_PreLogin_SendHandshake);
        }
        internal static string Timeout_PreLogin_ConsumeHandshake()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_PreLogin_ConsumeHandshake);
        }
        internal static string Timeout_Login_Begin()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_Login_Begin);
        }
        internal static string Timeout_Login_ProcessConnectionAuth()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_Login_ProcessConnectionAuth);
        }
        internal static string Timeout_PostLogin()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_PostLogin);
        }
        internal static string Timeout_FailoverInfo()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_FailoverInfo);
        }
        internal static string Timeout_RoutingDestination()
        {
            return System.SRHelper.GetString(SR.SQL_Timeout_RoutingDestinationInfo);
        }
        internal static string Duration_PreLogin_Begin(long PreLoginBeginDuration)
        {
            return System.SRHelper.GetString(SR.SQL_Duration_PreLogin_Begin, PreLoginBeginDuration);
        }
        internal static string Duration_PreLoginHandshake(long PreLoginBeginDuration, long PreLoginHandshakeDuration)
        {
            return System.SRHelper.GetString(SR.SQL_Duration_PreLoginHandshake, PreLoginBeginDuration, PreLoginHandshakeDuration);
        }
        internal static string Duration_Login_Begin(long PreLoginBeginDuration, long PreLoginHandshakeDuration, long LoginBeginDuration)
        {
            return System.SRHelper.GetString(SR.SQL_Duration_Login_Begin, PreLoginBeginDuration, PreLoginHandshakeDuration, LoginBeginDuration);
        }
        internal static string Duration_Login_ProcessConnectionAuth(long PreLoginBeginDuration, long PreLoginHandshakeDuration, long LoginBeginDuration, long LoginAuthDuration)
        {
            return System.SRHelper.GetString(SR.SQL_Duration_Login_ProcessConnectionAuth, PreLoginBeginDuration, PreLoginHandshakeDuration, LoginBeginDuration, LoginAuthDuration);
        }
        internal static string Duration_PostLogin(long PreLoginBeginDuration, long PreLoginHandshakeDuration, long LoginBeginDuration, long LoginAuthDuration, long PostLoginDuration)
        {
            return System.SRHelper.GetString(SR.SQL_Duration_PostLogin, PreLoginBeginDuration, PreLoginHandshakeDuration, LoginBeginDuration, LoginAuthDuration, PostLoginDuration);
        }
        internal static string UserInstanceFailure()
        {
            return System.SRHelper.GetString(SR.SQL_UserInstanceFailure);
        }
        internal static string PreloginError()
        {
            return System.SRHelper.GetString(SR.Snix_PreLogin);
        }
        internal static string ExClientConnectionId()
        {
            return System.SRHelper.GetString(SR.SQL_ExClientConnectionId);
        }
        internal static string ExErrorNumberStateClass()
        {
            return System.SRHelper.GetString(SR.SQL_ExErrorNumberStateClass);
        }
        internal static string ExOriginalClientConnectionId()
        {
            return System.SRHelper.GetString(SR.SQL_ExOriginalClientConnectionId);
        }
        internal static string ExRoutingDestination()
        {
            return System.SRHelper.GetString(SR.SQL_ExRoutingDestination);
        }
    }

    /// <summary>
    /// This class holds helper methods to escape Microsoft SQL Server identifiers, such as table, schema, database or other names
    /// </summary>
    internal static class SqlServerEscapeHelper
    {
        /// <summary>
        /// Escapes the identifier with square brackets. The input has to be in unescaped form, like the parts received from MultipartIdentifier.ParseMultipartIdentifier.
        /// </summary>
        /// <param name="name">name of the identifier, in unescaped form</param>
        /// <returns>escapes the name with [], also escapes the last close bracket with double-bracket</returns>
        internal static string EscapeIdentifier(string name)
        {
            Debug.Assert(!string.IsNullOrEmpty(name), "null or empty identifiers are not allowed");
            return "[" + name.Replace("]", "]]") + "]";
        }

        /// <summary>
        /// Same as above EscapeIdentifier, except that output is written into StringBuilder
        /// </summary>
        internal static void EscapeIdentifier(StringBuilder builder, string name)
        {
            Debug.Assert(builder != null, "builder cannot be null");
            Debug.Assert(!string.IsNullOrEmpty(name), "null or empty identifiers are not allowed");

            builder.Append('[');
            builder.Append(name.Replace("]", "]]"));
            builder.Append(']');
        }

        /// <summary>
        ///  Escape a string to be used inside TSQL literal, such as N'somename' or 'somename'
        /// </summary>
        internal static string EscapeStringAsLiteral(string input)
        {
            Debug.Assert(input != null, "input string cannot be null");
            return input.Replace("'", "''");
        }

        /// <summary>
        /// Escape a string as a TSQL literal, wrapping it around with single quotes.
        /// Use this method to escape input strings to prevent SQL injection 
        /// and to get correct behavior for embedded quotes.
        /// </summary>
        /// <param name="input">unescaped string</param>
        /// <returns>escaped and quoted literal string</returns>
        internal static string MakeStringLiteral(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "''";
            }
            else
            {
                return "'" + EscapeStringAsLiteral(input) + "'";
            }
        }
    }

    /// <summary>
    /// This class holds methods invoked on System.Transactions through reflection for Global Transactions
    /// </summary>
    internal static class SysTxForGlobalTransactions
    {
        private static readonly Lazy<MethodInfo> _enlistPromotableSinglePhase = new Lazy<MethodInfo>(() =>
            typeof(Transaction).GetMethod("EnlistPromotableSinglePhase", new Type[] { typeof(IPromotableSinglePhaseNotification), typeof(Guid) }));

        private static readonly Lazy<MethodInfo> _setDistributedTransactionIdentifier = new Lazy<MethodInfo>(() =>
            typeof(Transaction).GetMethod("SetDistributedTransactionIdentifier", new Type[] { typeof(IPromotableSinglePhaseNotification), typeof(Guid) }));

        private static readonly Lazy<MethodInfo> _getPromotedToken = new Lazy<MethodInfo>(() =>
            typeof(Transaction).GetMethod("GetPromotedToken"));

        /// <summary>
        /// Enlists the given IPromotableSinglePhaseNotification and Non-MSDTC Promoter type into a transaction
        /// </summary>
        /// <returns>The MethodInfo instance to be invoked. Null if the method doesn't exist</returns>
        public static MethodInfo EnlistPromotableSinglePhase
        {
            get
            {
                return _enlistPromotableSinglePhase.Value;
            }
        }

        /// <summary>
        /// Sets the given DistributedTransactionIdentifier for a Transaction instance.
        /// Needs to be invoked when using a Non-MSDTC Promoter type
        /// </summary>
        /// <returns>The MethodInfo instance to be invoked. Null if the method doesn't exist</returns>
        public static MethodInfo SetDistributedTransactionIdentifier
        {
            get
            {
                return _setDistributedTransactionIdentifier.Value;
            }
        }

        /// <summary>
        /// Gets the Promoted Token for a Transaction
        /// </summary>
        /// <returns>The MethodInfo instance to be invoked. Null if the method doesn't exist</returns>
        public static MethodInfo GetPromotedToken
        {
            get
            {
                return _getPromotedToken.Value;
            }
        }
    }
}//namespace
