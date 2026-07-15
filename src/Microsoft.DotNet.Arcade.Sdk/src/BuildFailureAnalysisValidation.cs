// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.Arcade.Sdk
{
    // NOTE: This file intentionally introduces a compile error (CS0103) to
    // validate the Build Failure Analysis agentic workflow (dotnet/arcade
    // PR #17125). It must never be merged into a real branch.
    internal static class BuildFailureAnalysisValidation
    {
        public static int GetValue()
        {
            // `ThisSymbolDoesNotExist` is not declared anywhere, so the C#
            // compiler reports: error CS0103: The name
            // 'ThisSymbolDoesNotExist' does not exist in the current context.
            return ThisSymbolDoesNotExist;
        }
    }
}
