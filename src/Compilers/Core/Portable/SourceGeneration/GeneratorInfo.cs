﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Microsoft.CodeAnalysis
{
    internal readonly struct GeneratorInfo
    {
        internal SyntaxContextReceiverCreator? SyntaxContextReceiverCreator { get; }

        internal Action<GeneratorPostInitializationContext>? PostInitCallback { get; }

        internal bool Initialized { get; }

        internal GeneratorInfo(SyntaxContextReceiverCreator? receiverCreator, Action<GeneratorPostInitializationContext>? postInitCallback)
        {
            SyntaxContextReceiverCreator = receiverCreator;
            PostInitCallback = postInitCallback;
            Initialized = true;
        }

        internal class Builder
        {
            internal SyntaxContextReceiverCreator? SyntaxContextReceiverCreator { get; set; }

            internal Action<GeneratorPostInitializationContext>? PostInitCallback { get; set; }

            public GeneratorInfo ToImmutable() => new GeneratorInfo(SyntaxContextReceiverCreator, PostInitCallback);
        }
    }
}
