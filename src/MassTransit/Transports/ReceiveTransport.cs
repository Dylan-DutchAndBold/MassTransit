namespace MassTransit.Transports
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Configuration;
    using Context;
    using Events;
    using GreenPipes;
    using GreenPipes.Agents;


    public class ReceiveTransport<TContext> :
        IReceiveTransport
        where TContext : class, PipeContext
    {
        readonly ReceiveEndpointContext _context;
        readonly IHostConfiguration _hostConfiguration;
        readonly Func<ISupervisor<TContext>> _supervisorFactory;
        readonly IPipe<TContext> _transportPipe;

        public ReceiveTransport(IHostConfiguration hostConfiguration, ReceiveEndpointContext context, Func<ISupervisor<TContext>> supervisorFactory,
            IPipe<TContext> transportPipe)
        {
            _hostConfiguration = hostConfiguration;
            _context = context;
            _supervisorFactory = supervisorFactory;
            _transportPipe = transportPipe;
        }

        void IProbeSite.Probe(ProbeContext context)
        {
            var scope = context.CreateScope("receiveTransport");

            _context.Probe(scope);
        }

        /// <summary>
        /// Start the receive transport, returning a Task that can be awaited to signal the transport has
        /// completely shutdown once the cancellation token is cancelled.
        /// </summary>
        /// <returns>A task that is completed once the transport is shut down</returns>
        public ReceiveTransportHandle Start()
        {
            return new ReceiveTransportAgent(_hostConfiguration.ReceiveTransportRetryPolicy, _context, _supervisorFactory, _transportPipe);
        }

        ConnectHandle IReceiveObserverConnector.ConnectReceiveObserver(IReceiveObserver observer)
        {
            return _context.ConnectReceiveObserver(observer);
        }

        ConnectHandle IReceiveTransportObserverConnector.ConnectReceiveTransportObserver(IReceiveTransportObserver observer)
        {
            return _context.ConnectReceiveTransportObserver(observer);
        }

        ConnectHandle IPublishObserverConnector.ConnectPublishObserver(IPublishObserver observer)
        {
            return _context.ConnectPublishObserver(observer);
        }

        ConnectHandle ISendObserverConnector.ConnectSendObserver(ISendObserver observer)
        {
            return _context.ConnectSendObserver(observer);
        }


        class ReceiveTransportAgent :
            Agent,
            ReceiveTransportHandle
        {
            readonly ReceiveEndpointContext _context;
            readonly IRetryPolicy _retryPolicy;
            readonly Func<ISupervisor<TContext>> _supervisorFactory;
            readonly IPipe<TContext> _transportPipe;
            ISupervisor<TContext> _supervisor;

            public ReceiveTransportAgent(IRetryPolicy retryPolicy, ReceiveEndpointContext context, Func<ISupervisor<TContext>> supervisorFactory,
                IPipe<TContext> transportPipe)
            {
                _retryPolicy = retryPolicy;
                _context = context;
                _supervisorFactory = supervisorFactory;
                _transportPipe = transportPipe;

                var receiver = Task.Run(Run);

                SetReady(receiver);

                SetCompleted(receiver);
            }

            Task ReceiveTransportHandle.Stop(CancellationToken cancellationToken)
            {
                return this.Stop("Stop Receive Transport", cancellationToken);
            }

            protected override async Task StopAgent(StopContext context)
            {
                LogContext.SetCurrentIfNull(_context.LogContext);

                TransportLogMessages.StoppingReceiveTransport(_context.InputAddress);

                if (_supervisor != null)
                    await _supervisor.Stop(context).ConfigureAwait(false);

                await base.StopAgent(context).ConfigureAwait(false);
            }

            async Task Run()
            {
                var stoppingContext = new TransportStoppingContext(Stopping);

                RetryPolicyContext<TransportStoppingContext> policyContext = _retryPolicy.CreatePolicyContext(stoppingContext);

                try
                {
                    RetryContext<TransportStoppingContext> retryContext = null;

                    while (!IsStopping)
                    {
                        try
                        {
                            if (retryContext != null)
                            {
                                LogContext.Warning?.Log(retryContext.Exception, "Retrying {Delay}: {Message}", retryContext.Delay,
                                    retryContext.Exception.Message);

                                if (retryContext.Delay.HasValue)
                                    await Task.Delay(retryContext.Delay.Value, Stopping).ConfigureAwait(false);
                            }

                            if (!IsStopping)
                                await RunTransport().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException exception)
                        {
                            if(retryContext == null)
                                await NotifyFaulted(exception).ConfigureAwait(false);

                            throw;
                        }
                        catch (Exception exception)
                        {
                            if (retryContext != null)
                            {
                                retryContext = retryContext.CanRetry(exception, out RetryContext<TransportStoppingContext> nextRetryContext)
                                    ? nextRetryContext
                                    : null;
                            }

                            if (retryContext == null && !policyContext.CanRetry(exception, out retryContext))
                                break;
                        }
                    }
                }
                catch (Exception exception)
                {
                    LogContext.Debug?.Log(exception, "ReceiveTransport Run Exception: {InputAddress}", _context.InputAddress);
                }
                finally
                {
                    policyContext.Dispose();
                }
            }

            async Task RunTransport()
            {
                try
                {
                    _supervisor = _supervisorFactory();

                    await _context.OnTransportStartup(_supervisor, Stopping).ConfigureAwait(false);

                    if (!IsStopping)
                        await _supervisor.Send(_transportPipe, Stopped).ConfigureAwait(false);
                }
                catch (ConnectionException exception)
                {
                    await NotifyFaulted(exception).ConfigureAwait(false);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw await NotifyFaulted(exception, "ReceiveTransport faulted: ").ConfigureAwait(false);
                }
            }

            async Task<Exception> NotifyFaulted(Exception originalException, string message)
            {
                var exception = _context.ConvertException(originalException, message);

                await NotifyFaulted(exception).ConfigureAwait(false);

                return exception;
            }

            Task NotifyFaulted(Exception exception)
            {
                LogContext.Error?.Log(exception, "Connection Failed: {InputAddress}", _context.InputAddress);

                return _context.TransportObservers.Faulted(new ReceiveTransportFaultedEvent(_context.InputAddress, exception));
            }


            class TransportStoppingContext :
                BasePipeContext,
                PipeContext
            {
                public TransportStoppingContext(CancellationToken cancellationToken)
                    : base(cancellationToken)
                {
                }
            }
        }
    }
}
