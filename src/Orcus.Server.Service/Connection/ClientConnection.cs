﻿using Orcus.Server.Library.Services;
using Orcus.Server.OrcusSockets;

namespace Orcus.Server.Service.Connection
{
    public class ClientConnection : IClientConnection
    {
        public ClientConnection(int clientId, OrcusServer orcusServer)
        {
            ClientId = clientId;
            OrcusServer = orcusServer;
        }

        public int ClientId { get; }
        public OrcusServer OrcusServer { get; }
    }
}