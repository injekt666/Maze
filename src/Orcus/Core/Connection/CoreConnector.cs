﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.Extensions.Options;
using Orcus.Core.Modules;
using Orcus.Logging;
using Orcus.Options;

namespace Orcus.Core.Connection
{
    public class CoreConnector
    {
        private static readonly ILog Logger = LogProvider.For<CoreConnector>();

        private readonly IServerConnector _serverConnector;
        private readonly IModuleDownloader _moduleDownloader;
        private readonly ConnectionOptions _options;

        public CoreConnector(IOptions<ConnectionOptions> options, IServerConnector serverConnector, IModuleDownloader moduleDownloader)
        {
            _serverConnector = serverConnector;
            _moduleDownloader = moduleDownloader;
            _options = options.Value;
        }

        public ServerConnection CurrentConnection { get; private set; }

        public async Task StartConnecting(ILifetimeScope lifetimeScope)
        {
            var uris = new List<Uri>();
            foreach (var serverUri in _options.ServerUris)
            {
                if (Uri.TryCreate(serverUri, UriKind.Absolute, out var uri))
                    uris.Add(uri);
                else Logger.Warn("Unable to parse uri {uri}", serverUri);
            }

            while (true)
            {
                foreach (var serverUri in uris)
                {
                    Logger.Debug("Try to connect to {uri}", serverUri);
                    try
                    {
                        var connection = await _serverConnector.TryConnect(serverUri);
                        Logger.Debug("Connection succeeded, initialize modules");

                        await _moduleDownloader.Load(connection.PackagesLock, connection, CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        Logger.Warn(e, "Error occurred when trying to connect to {uri}", serverUri);
                    }
                }

                await Task.Delay(_options.ReconnectDelay);
            }
        }
    }
}