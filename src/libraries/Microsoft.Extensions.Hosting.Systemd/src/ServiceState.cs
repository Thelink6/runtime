// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Text;

namespace Microsoft.Extensions.Hosting.Systemd
{
    /// <summary>
    /// Describes a service state change.
    /// </summary>
    public struct ServiceState
    {
        private readonly byte[] _data;

        /// <summary>
        /// Service startup is finished.
        /// </summary>
        public static readonly ServiceState Ready = new ServiceState("READY=1");

        /// <summary>
        /// Service is beginning its shutdown.
        /// </summary>
        public static readonly ServiceState Stopping = new ServiceState("STOPPING=1");

        /// <summary>
        /// Create custom ServiceState.
        /// </summary>
        /// <param name="state">A <see cref="string"/> representation of service state.</param>
        public ServiceState(string state)
        {
            ArgumentNullException.ThrowIfNull(state);

            _data = Encoding.UTF8.GetBytes(state);
        }

        /// <summary>
        /// String representation of service state.
        /// </summary>
        /// <returns>The <see cref="string"/> representation of the service state.</returns>
        public override string ToString()
            => _data == null ? string.Empty : Encoding.UTF8.GetString(_data);

        internal byte[] GetData() => _data;
    }
}
