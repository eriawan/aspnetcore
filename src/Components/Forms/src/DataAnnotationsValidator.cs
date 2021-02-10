// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics.CodeAnalysis;
using static Microsoft.AspNetCore.Internal.LinkerFlags;

namespace Microsoft.AspNetCore.Components.Forms
{
    /// <summary>
    /// Adds Data Annotations validation support to an <see cref="EditContext"/>.
    /// </summary>
    public class DataAnnotationsValidator : ComponentBase
    {
        [CascadingParameter] EditContext? CurrentEditContext { get; set; }

        /// <inheritdoc />
        [DynamicDependency(BlazorComponent, typeof(DataAnnotationsValidator))]
        protected override void OnInitialized()
        {
            if (CurrentEditContext == null)
            {
                throw new InvalidOperationException($"{nameof(DataAnnotationsValidator)} requires a cascading " +
                    $"parameter of type {nameof(EditContext)}. For example, you can use {nameof(DataAnnotationsValidator)} " +
                    $"inside an EditForm.");
            }

            CurrentEditContext.AddDataAnnotationsValidation();
        }
    }
}
