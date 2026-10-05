using System;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Plugins;
using AC.Host.Runtime;

namespace Decal.Agent
{
    /// <summary>
    /// The host the Agent runs: started when the Agent starts, started again when Options change
    /// the ports, stopped when it exits. Holds nothing but the one <see cref="HostRuntime"/>, which
    /// is exactly what <c>achost run --overlay</c> runs.
    /// </summary>
    /// <remarks>
    /// Everything slow - opening the client's data, loading plugins, binding the relay's ports - is
    /// done off the window's thread, so the window stays responsive while the host comes up.
    /// </remarks>
    internal sealed class AgentHost
    {
        private readonly IPluginLog _log;
        private HostRuntime _runtime;

        public AgentHost(IPluginLog log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>The running host, or null while it is starting, stopped, or could not start.</summary>
        public HostRuntime Runtime => Volatile.Read(ref _runtime);

        /// <summary>Why the host is not running, in a sentence, or null.</summary>
        public string Problem { get; private set; }

        /// <summary>True between asking the host to start and it having started or failed.</summary>
        public bool Starting { get; private set; }

        /// <summary>Starts a host with these options, unless one is running; says why not in <see cref="Problem"/>.</summary>
        public async Task<bool> StartAsync(HostRuntimeOptions options)
        {
            if (Runtime != null || Starting)
                return Runtime != null;

            Starting = true;
            Problem = null;
            try
            {
                HostRuntime started = await Task.Run(async () =>
                {
                    HostRuntime runtime = new HostRuntime(options, _log);
                    try
                    {
                        await runtime.StartAsync().ConfigureAwait(false);
                        return runtime;
                    }
                    catch
                    {
                        await runtime.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }).ConfigureAwait(true);

                Volatile.Write(ref _runtime, started);
                _log.Info("Decal Agent: the host is running.");
                return true;
            }
            catch (Exception ex)
            {
                Problem = ex.Message;
                _log.Error("Decal Agent: the host could not start.", ex);
                return false;
            }
            finally
            {
                Starting = false;
            }
        }

        /// <summary>Stops the host, saving every plugin's settings on the way. Harmless when none is running.</summary>
        public async Task StopAsync()
        {
            HostRuntime runtime = Interlocked.Exchange(ref _runtime, null);
            if (runtime == null)
                return;

            await Task.Run(() => runtime.DisposeAsync().AsTask()).ConfigureAwait(true);
            _log.Info("Decal Agent: the host has stopped.");
        }

        /// <summary>
        /// Runs something on the host's game thread - the only place a plugin may be touched - and
        /// hands back its result. Null, without running it, when no host is running.
        /// </summary>
        public Task<T> OnGameThread<T>(Func<HostRuntime, T> work)
        {
            HostRuntime runtime = Runtime;
            if (runtime == null)
                return Task.FromResult(default(T));

            TaskCompletionSource<T> done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Host.RunOnGameThread(() =>
            {
                try
                {
                    done.TrySetResult(work(runtime));
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            });

            // A host stopping underneath the request drops it; nobody should wait forever for that.
            return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
