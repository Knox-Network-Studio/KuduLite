﻿
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Kudu.Contracts.Tracing;
using Kudu.Core.Tracing;
using Microsoft.Extensions.Hosting;


namespace Kudu.Services.Performance
{

    /// <summary>
    /// ASP.NET core background HostedService that manages sessions
    /// submitted for DAAS for Linux apps
    /// </summary>
    public class SessionRunnerService : BackgroundService
    {
        private const double MaxAllowedSessionTimeInMinutes = 15;

        private readonly ITracer _tracer;
        private readonly ITraceFactory _traceFactory;
        private readonly ISessionManager _sessionManager;
        private readonly Dictionary<string, TaskAndCancellationToken> _runningSessions = new Dictionary<string, TaskAndCancellationToken>();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="traceFactory"></param>
        /// <param name="sessionManager"></param>
        public SessionRunnerService(ITraceFactory traceFactory,
            ISessionManager sessionManager)
        {
            _sessionManager = sessionManager;
            _traceFactory = traceFactory;
            _tracer = _traceFactory.GetTracer();
        }

        /// <summary>
        /// Implement abstract ExecuteAsync method for BackgroundService
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DotNetHelper.IsDotNetMonitorEnabled())
                {
                    await SessionRunner(stoppingToken);
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }

        private async Task SessionRunner(CancellationToken stoppingToken)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await RunActiveSession();

            CleanupCompletedSessions();
        }

        private void CleanupCompletedSessions()
        {
            foreach (var sessionId in _runningSessions.Keys.ToList())
            {
                if (_runningSessions[sessionId].UnderlyingTask != null)
                {
                    var status = _runningSessions[sessionId].UnderlyingTask.Status;
                    if (status == TaskStatus.Canceled || status == TaskStatus.Faulted || status == TaskStatus.RanToCompletion)
                    {

                        TraceExtensions.Trace(_tracer, $"Task for Session {sessionId} has completed with status {status} on {System.Environment.MachineName}", sessionId.ToString());
                        _runningSessions.Remove(sessionId);
                    }
                }
            }
        }

        private async Task RunActiveSession()
        {
            Session activeSession = await _sessionManager.GetActiveSession();
            if (activeSession == null)
            {
                return;
            }

            // Check if all instances are finished with log collection
            if (await CheckandCompleteSessionIfNeeded(activeSession))
            {
                return;
            }

            if (DateTime.UtcNow.Subtract(activeSession.StartTime).TotalMinutes > MaxAllowedSessionTimeInMinutes)
            {
                await CheckandCompleteSessionIfNeeded(activeSession, forceCompletion: true);
            }

            if (_sessionManager.ShouldCollectOnCurrentInstance(activeSession))
            {
                if (_runningSessions.ContainsKey(activeSession.SessionId))
                {
                    // data Collection for this session is in progress
                    return;
                }

                if (_sessionManager.HasThisInstanceCollectedLogs(activeSession))
                {
                    // This instance has already collected logs for this session
                    return;
                }

                CancellationTokenSource cts = new CancellationTokenSource();
                var sessionTask = RunToolForSessionAsync(activeSession, cts.Token);

                TaskAndCancellationToken t = new TaskAndCancellationToken
                {
                    UnderlyingTask = sessionTask,
                    CancellationSource = cts
                };

                _runningSessions[activeSession.SessionId] = t;
            }
        }

        private async Task<bool> CheckandCompleteSessionIfNeeded(Session activeSession, bool forceCompletion = false)
        {
            if (_sessionManager.AllInstancesCollectedLogs(activeSession) || forceCompletion)
            {
                await _sessionManager.MarkSessionAsComplete(activeSession);
                return true;
            }

            return false;
        }

        private async Task RunToolForSessionAsync(Session activeSession, CancellationToken token)
        {
            IDiagnosticTool diagnosticTool = GetDiagnosticTool(activeSession);
            await _sessionManager.MarkCurrentInstanceAsStarted(activeSession);

            TraceExtensions.Trace(_tracer, $"Invoking Diagnostic tool for session {activeSession.SessionId}");
            var logs = await diagnosticTool.InvokeAsync(activeSession.ToolParams);
            {
                await AddLogsToActiveSession(activeSession, logs);
            }

            await _sessionManager.MarkCurrentInstanceAsComplete(activeSession);
            await CheckandCompleteSessionIfNeeded(activeSession);
        }

        private static IDiagnosticTool GetDiagnosticTool(Session activeSession)
        {
            IDiagnosticTool diagnosticTool;
            if (activeSession.Tool == DiagnosticTool.MemoryDump)
            {
                diagnosticTool = new MemoryDumpTool();
            }
            else if (activeSession.Tool == DiagnosticTool.Profiler)
            {
                diagnosticTool = new ClrTraceTool();
            }
            else
            {
                throw new ApplicationException($"Diagnostic Tool of type {activeSession.Tool} not found");
            }

            return diagnosticTool;
        }

        private async Task AddLogsToActiveSession(Session activeSession, IEnumerable<LogFile> logs)
        {
            foreach (var log in logs)
            {
                log.Size = GetFileSize(log.FullPath);
                log.Name = Path.GetFileName(log.FullPath);
            }

            await _sessionManager.AddLogsToActiveSession(activeSession, logs);
        }

        private long GetFileSize(string path)
        {
            return new FileInfo(path).Length;
        }
    }
}