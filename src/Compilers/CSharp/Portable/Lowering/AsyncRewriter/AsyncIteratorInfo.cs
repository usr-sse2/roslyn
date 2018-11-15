﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Additional information for rewriting an async-iterator.
    /// </summary>
    internal sealed class AsyncIteratorInfo
    {
        // This `ManualResetValueTaskSourceLogic<bool>` struct implements the `IValueTaskSource` logic
        internal FieldSymbol PromiseOfValueOrEndField { get; }

        // Stores the current/yielded value
        internal FieldSymbol CurrentField { get; }

        // Method to fulfill the promise with a result: `void ManualResetValueTaskSourceLogic<T>.SetResult(T result)`
        internal MethodSymbol SetResultMethod { get; }

        // Method to fulfill the promise with an exception: `void ManualResetValueTaskSourceLogic<T>.SetException(Exception error)`
        internal MethodSymbol SetExceptionMethod { get; }

        public AsyncIteratorInfo(FieldSymbol promiseOfValueOrEndField, FieldSymbol currentField,
            MethodSymbol setResultMethod, MethodSymbol setExceptionMethod)
        {
            PromiseOfValueOrEndField = promiseOfValueOrEndField;
            CurrentField = currentField;
            SetResultMethod = setResultMethod;
            SetExceptionMethod = setExceptionMethod;
        }
    }
}
