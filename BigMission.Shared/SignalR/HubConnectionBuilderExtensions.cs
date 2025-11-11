using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BigMission.Shared.SignalR;

/// <summary>
/// Hub connection builder extensions for configuring SignalR protocols.
/// </summary>
public static class HubConnectionBuilderExtensions
{
    /// <summary>
    /// Adds the appropriate protocol to the SignalR hub connection.
    /// Uses JSON protocol on iOS due to MessagePack AOT limitations with SignalR parameter serialization.
    /// MessagePack works fine for REST API responses but SignalR's InvokeAsync uses reflection-based
    /// serialization for parameters which fails on iOS.
    /// </summary>
    public static IHubConnectionBuilder TryAddMessagePack(this IHubConnectionBuilder builder)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS")))
        {
            builder.AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                };
            });
        }
        else
        {
            builder.AddMessagePackProtocol();
        }

        return builder;
    }
}