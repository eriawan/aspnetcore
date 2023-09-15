// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Microsoft.AspNetCore.Internal.LinkerFlags;

namespace Microsoft.AspNetCore.Components.Endpoints;

/// <summary>
/// A component that describes a location in prerendered output where client-side code
/// should insert an interactive component.
/// </summary>
internal class SSRRenderModeBoundary : IComponent
{
    private static readonly ConcurrentDictionary<Type, string> _componentTypeNameHashCache = new();

    [DynamicallyAccessedMembers(Component)]
    private readonly Type _componentType;
    private readonly IComponentRenderMode _renderMode;
    private readonly bool _prerender;
    private RenderHandle _renderHandle;
    private IReadOnlyDictionary<string, object?>? _latestParameters;
    private string? _markerKey;

    public SSRRenderModeBoundary(
        HttpContext httpContext,
        [DynamicallyAccessedMembers(Component)] Type componentType,
        IComponentRenderMode renderMode)
    {
        AssertRenderModeIsConfigured(httpContext, componentType, renderMode);

        _componentType = componentType;
        _renderMode = renderMode;
        _prerender = renderMode switch
        {
            InteractiveServerRenderMode mode => mode.Prerender,
            InteractiveWebAssemblyRenderMode mode => mode.Prerender,
            InteractiveAutoRenderMode mode => mode.Prerender,
            _ => throw new ArgumentException($"Server-side rendering does not support the render mode '{renderMode}'.", nameof(renderMode))
        };
    }

    private static void AssertRenderModeIsConfigured(HttpContext httpContext, Type componentType, IComponentRenderMode renderMode)
    {
        var configuredRenderModesMetadata = httpContext.GetEndpoint()?.Metadata.GetMetadata<ConfiguredRenderModesMetadata>();
        if (configuredRenderModesMetadata is null)
        {
            // This is not a Razor Components endpoint. It might be that the app is using RazorComponentResult,
            // or perhaps something else has changed the endpoint dynamically. In this case we don't know how
            // the app is configured so we just proceed and allow any errors to happen if the client-side code
            // later tries to reach endpoints that aren't mapped.
            return;
        }

        var configuredModes = configuredRenderModesMetadata.ConfiguredRenderModes;

        // We have to allow for specified rendermodes being subclases of the known types
        if (renderMode is InteractiveServerRenderMode || renderMode is InteractiveAutoRenderMode)
        {
            AssertRenderModeIsConfigured<InteractiveServerRenderMode>(componentType, renderMode, configuredModes, "AddInteractiveServerRenderMode");
        }

        if (renderMode is InteractiveWebAssemblyRenderMode || renderMode is InteractiveAutoRenderMode)
        {
            AssertRenderModeIsConfigured<InteractiveWebAssemblyRenderMode>(componentType, renderMode, configuredModes, "AddInteractiveWebAssemblyRenderMode");
        }
    }

    private static void AssertRenderModeIsConfigured<TRequiredMode>(Type componentType, IComponentRenderMode specifiedMode, IComponentRenderMode[] configuredModes, string expectedCall) where TRequiredMode: IComponentRenderMode
    {
        foreach (var configuredMode in configuredModes)
        {
            // We have to allow for configured rendermodes being subclases of the known types
            if (configuredMode is TRequiredMode)
            {
                return;
            }
        }

        throw new InvalidOperationException($"A component of type '{componentType}' has render mode '{specifiedMode.GetType().Name}', " +
            $"but the required endpoints are not mapped on the server. When calling " +
            $"'{nameof(RazorComponentsEndpointRouteBuilderExtensions.MapRazorComponents)}', add a call to " +
            $"'{expectedCall}'. For example, " +
            $"'builder.{nameof(RazorComponentsEndpointRouteBuilderExtensions.MapRazorComponents)}<...>.{expectedCall}()'");
    }

    public void Attach(RenderHandle renderHandle)
    {
        _renderHandle = renderHandle;
    }

    public Task SetParametersAsync(ParameterView parameters)
    {
        // We have to snapshot the parameters because ParameterView is like a ref struct - it can't escape the
        // call stack because the underlying buffer may get reused. This is enforced through a runtime check.
        _latestParameters = parameters.ToDictionary();

        ValidateParameters(_latestParameters);

        if (_prerender)
        {
            _renderHandle.Render(Prerender);
        }

        return Task.CompletedTask;
    }

    private void ValidateParameters(IReadOnlyDictionary<string, object?> latestParameters)
    {
        foreach (var (name, value) in latestParameters)
        {
            // There are many other things we can't serialize too, but give special errors for Delegate because
            // it may be a common mistake to try passing ChildContent when crossing rendermode boundaries.
            if (value is Delegate)
            {
                var valueType = value.GetType();
                if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(RenderFragment<>))
                {
                    throw new InvalidOperationException($"Cannot pass RenderFragment<T> parameter '{name}' to component '{_componentType.Name}' with rendermode '{_renderMode.GetType().Name}'. Templated content can't be passed across a rendermode boundary, because it is arbitrary code and cannot be serialized.");
                }
                else
                {
                    // TODO: Ideally we *should* support RenderFragment (the non-generic version) by prerendering it
                    // However it's very nontrivial since it means we have to execute it within the current renderer
                    // somehow without actually emitting its result directly, wait for quiescence, and then prerender
                    // the output into a separate buffer so we can serialize it in a special way.
                    // A prototype implementation is at https://github.com/dotnet/aspnetcore/commit/ed330ff5b143974d9060828a760ad486b1d386ac
                    throw new InvalidOperationException($"Cannot pass the parameter '{name}' to component '{_componentType.Name}' with rendermode '{_renderMode.GetType().Name}'. This is because the parameter is of the delegate type '{value.GetType()}', which is arbitrary code and cannot be serialized.");
                }
            }
        }
    }

    private void Prerender(RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, _componentType);

        foreach (var (name, value) in _latestParameters!)
        {
            builder.AddComponentParameter(1, name, value);
        }

        builder.CloseComponent();
    }

    public ComponentMarker ToMarker(HttpContext httpContext, int sequence, object? key)
    {
        // We expect that the '@key' and sequence number shouldn't change for a given component instance,
        // so we lazily compute the marker key once.
        _markerKey ??= GenerateMarkerKey(sequence, key);

        var parameters = _latestParameters is null
            ? ParameterView.Empty
            : ParameterView.FromDictionary((IDictionary<string, object?>)_latestParameters);

        var marker = _renderMode switch
        {
            InteractiveServerRenderMode server => ComponentMarker.Create(ComponentMarker.ServerMarkerType, server.Prerender, _markerKey),
            InteractiveWebAssemblyRenderMode webAssembly => ComponentMarker.Create(ComponentMarker.WebAssemblyMarkerType, webAssembly.Prerender, _markerKey),
            InteractiveAutoRenderMode auto => ComponentMarker.Create(ComponentMarker.AutoMarkerType, auto.Prerender, _markerKey),
            _ => throw new UnreachableException($"Unknown render mode {_renderMode.GetType().FullName}"),
        };

        if (_renderMode is InteractiveServerRenderMode or InteractiveAutoRenderMode)
        {
            // Lazy because we don't actually want to require a whole chain of services including Data Protection
            // to be required unless you actually use Server render mode.
            var serverComponentSerializer = httpContext.RequestServices.GetRequiredService<ServerComponentSerializer>();

            var invocationId = EndpointHtmlRenderer.GetOrCreateInvocationId(httpContext);
            serverComponentSerializer.SerializeInvocation(ref marker, invocationId, _componentType, parameters);
        }

        if (_renderMode is InteractiveWebAssemblyRenderMode or InteractiveAutoRenderMode)
        {
            WebAssemblyComponentSerializer.SerializeInvocation(ref marker, _componentType, parameters);
        }

        return marker;
    }

    private string GenerateMarkerKey(int sequence, object? key)
    {
        var componentTypeNameHash = _componentTypeNameHashCache.GetOrAdd(_componentType, ComputeComponentTypeNameHash);
        return $"{componentTypeNameHash}:{sequence}:{(key as IFormattable)?.ToString(null, CultureInfo.InvariantCulture)}";
    }

    private static string ComputeComponentTypeNameHash(Type componentType)
    {
        if (componentType.FullName is not { } typeName)
        {
            throw new InvalidOperationException($"An invalid component type was used in {nameof(SSRRenderModeBoundary)}.");
        }

        var typeNameLength = typeName.Length;
        var typeNameBytes = typeNameLength < 1024
            ? stackalloc byte[typeNameLength]
            : new byte[typeNameLength];

        Encoding.UTF8.GetBytes(typeName, typeNameBytes);

        Span<byte> typeNameHashBytes = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(typeNameBytes, typeNameHashBytes);

        return Convert.ToHexString(typeNameHashBytes);
    }
}
