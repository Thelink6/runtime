// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Net;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static partial class Ssl
    {
        [LibraryImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslCtxCreate")]
        internal static partial SafeSslContextHandle SslCtxCreate(IntPtr method);

        [LibraryImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslCtxDestroy")]
        internal static partial void SslCtxDestroy(IntPtr ctx);

        [LibraryImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslCtxGetData")]
        internal static partial IntPtr SslCtxGetData(IntPtr ctx);

        [LibraryImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslCtxSetData")]
        internal static partial int SslCtxSetData(SafeSslContextHandle ctx, IntPtr data);

        [LibraryImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslCtxSetData")]
        internal static partial int SslCtxSetData(IntPtr ctx, IntPtr data);

        [LibraryImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslCtxSetAlpnSelectCb")]
        internal static unsafe partial void SslCtxSetAlpnSelectCb(SafeSslContextHandle ctx, delegate* unmanaged<IntPtr, byte**, byte*, byte*, uint, IntPtr, int> callback, IntPtr arg);

        [LibraryImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslCtxSetKeylogCallback")]
        internal static unsafe partial void SslCtxSetKeylogCallback(SafeSslContextHandle ctx, delegate* unmanaged<IntPtr, char*, void> callback);

        [LibraryImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslCtxSetCaching")]
        internal static unsafe partial int SslCtxSetCaching(SafeSslContextHandle ctx, int mode, int cacheSize, int contextIdLength, Span<byte> contextId, delegate* unmanaged<IntPtr, IntPtr, int> neewSessionCallback, delegate* unmanaged<IntPtr, IntPtr, void> removeSessionCallback);

        [LibraryImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslCtxRemoveSession")]
        internal static unsafe partial void SslCtxRemoveSession(SafeSslContextHandle ctx, IntPtr session);

        internal static bool AddExtraChainCertificates(SafeSslContextHandle ctx, ReadOnlyCollection<X509Certificate2> chain)
        {
            // send pre-computed list of intermediates.
            for (int i = 0; i < chain.Count; i++)
            {
                SafeX509Handle dupCertHandle = Crypto.X509UpRef(chain[i].Handle);
                Crypto.CheckValidOpenSslHandle(dupCertHandle);
                if (!SslCtxAddExtraChainCert(ctx, dupCertHandle))
                {
                    Crypto.ErrClearError();
                    dupCertHandle.Dispose(); // we still own the safe handle; clean it up
                    return false;
                }
                dupCertHandle.SetHandleAsInvalid(); // ownership has been transferred to sslHandle; do not free via this safe handle
            }

            return true;
        }
    }
}

namespace Microsoft.Win32.SafeHandles
{
    internal sealed class SafeSslContextHandle : SafeHandle, ISafeHandleCachable
    {
        // This is session cache keyed by SNI e.g. TargetHost
        private Dictionary<string, IntPtr>? _sslSessions;
        private GCHandle _gch;

        // SSL_CTX handles are cached, so we need to keep track of the
        // number of times a handle is being used. Once we decide to dispose the handle,
        // we set the _rentCount to -1.
        private volatile int _rentCount;

        public SafeSslContextHandle()
            : base(IntPtr.Zero, true)
        {
        }

        internal SafeSslContextHandle(IntPtr handle, bool ownsHandle)
            : base(handle, ownsHandle)
        {
        }

        public override bool IsInvalid
        {
            get { return handle == IntPtr.Zero; }
        }

        public bool TryAddRentCount()
        {
            int oldCount;

            do
            {
                oldCount = _rentCount;
                if (oldCount < 0)
                {
                    // The handle is already disposed.
                    return false;
                }
            } while (Interlocked.CompareExchange(ref _rentCount, oldCount + 1, oldCount) != oldCount);

            return true;
        }

        public bool TryMarkForDispose()
        {
            return Interlocked.CompareExchange(ref _rentCount, -1, 0) == 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Decrement(ref _rentCount) < 0)
            {
                // _rentCount is 0 if the handle was never rented (e.g. failure during creation),
                // and is -1 when evicted from cache.
                base.Dispose(disposing);
            }
        }

        protected override bool ReleaseHandle()
        {
            if (_sslSessions != null)
            {
                // The SSL_CTX is ref counted and may not immediately die when we call SslCtxDestroy()
                // Since there is no relation between SafeSslContextHandle and SafeSslHandle `this`
                // can be released while we still have SSL session using it.
                Interop.Ssl.SslCtxSetData(handle, IntPtr.Zero);

                lock (_sslSessions)
                {
                    foreach (IntPtr session in _sslSessions.Values)
                    {
                        Interop.Ssl.SessionFree(session);
                    }

                    _sslSessions.Clear();
                }

                Debug.Assert(_gch.IsAllocated);
                _gch.Free();
            }

            Interop.Ssl.SslCtxDestroy(handle);
            SetHandle(IntPtr.Zero);

            return true;
        }

        internal void EnableSessionCache()
        {
            Debug.Assert(_sslSessions == null);

            _sslSessions = new Dictionary<string, IntPtr>();
            _gch = GCHandle.Alloc(this);
            Debug.Assert(_gch.IsAllocated);
            // This is needed so we can find the handle from session in SessionRemove callback.
            Interop.Ssl.SslCtxSetData(this, (IntPtr)_gch);
        }

        internal bool TryAddSession(IntPtr namePtr, IntPtr session)
        {
            Debug.Assert(_sslSessions != null && session != IntPtr.Zero);

            if (_sslSessions == null || namePtr == IntPtr.Zero)
            {
                return false;
            }

            string? targetName = Marshal.PtrToStringUTF8(namePtr);
            Debug.Assert(targetName != null);

            if (!string.IsNullOrEmpty(targetName))
            {
                // We do this only for lookup in RemoveSession.
                // Since this is part of cache manipulation and no function impact it is done here.
                // This will use strdup() so it is safe to pass in raw pointer.
                Interop.Ssl.SessionSetHostname(session, namePtr);

                IntPtr oldSession = IntPtr.Zero;

                lock (_sslSessions)
                {
                    if (!_sslSessions.TryAdd(targetName, session))
                    {
                        // session to this target host exists, replace it
                        _sslSessions.Remove(targetName, out oldSession);
                        bool added = _sslSessions.TryAdd(targetName, session);
                        Debug.Assert(added);
                    }
                }

                if (oldSession != IntPtr.Zero)
                {
                    // remove old session also from the internal OpenSSL cache
                    // and drop reference count. Since SSL_CTX_remove_session
                    // will call session_remove_cb, we need to do this outside
                    // of _sslSessions lock to avoid deadlock with another thread
                    // which could be holding SSL_CTX lock and trying to acquire
                    // _sslSessions lock.
                    Interop.Ssl.SslCtxRemoveSession(this, oldSession);
                    Interop.Ssl.SessionFree(oldSession);
                }

                return true;
            }

            return false;
        }

        internal void RemoveSession(IntPtr namePtr, IntPtr session)
        {
            Debug.Assert(_sslSessions != null);

            string? targetName = Marshal.PtrToStringUTF8(namePtr);
            Debug.Assert(targetName != null);

            if (_sslSessions != null && targetName != null)
            {
                IntPtr oldSession = IntPtr.Zero;
                bool removed = false;
                lock (_sslSessions)
                {
                    if (_sslSessions.TryGetValue(targetName, out IntPtr existingSession) && existingSession == session)
                    {
                        removed = _sslSessions.Remove(targetName, out oldSession);
                    }
                }

                if (removed)
                {
                    // It seems like we may be called more than once. Since we grabbed only one refference
                    // when added to Dictionary, we will also drop exactly one when removed.
                    Interop.Ssl.SessionFree(oldSession);
                }

            }
        }

        internal bool TrySetSession(SafeSslHandle sslHandle, string name)
        {
            Debug.Assert(_sslSessions != null);

            if (_sslSessions == null || string.IsNullOrEmpty(name))
            {
                return false;
            }

            // even if we don't have matching session, we can get new one and we need
            // way how to link SSL back to `this`.
            Debug.Assert(Interop.Ssl.SslGetData(sslHandle) == IntPtr.Zero);
            Interop.Ssl.SslSetData(sslHandle, (IntPtr)_gch);

            lock (_sslSessions)
            {
                if (_sslSessions.TryGetValue(name, out IntPtr session))
                {
                    // This will increase reference count on the session as needed.
                    // We need to hold lock here to prevent session being deleted before the call is done.
                    Interop.Ssl.SslSetSession(sslHandle, session);
                    return true;
                }
            }

            return false;
        }
    }
}
