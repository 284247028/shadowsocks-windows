﻿using NLog;
using Shadowsocks.Model;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Linq;

namespace Shadowsocks.Controller.Strategy
{
    class HighAvailabilityStrategy : IStrategy
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        protected ServerStatus _currentServer;
        protected Dictionary<Server, ServerStatus> _serverStatus;
        ShadowsocksController _controller;
        Random _random;

        public class ServerStatus
        {
            // time interval between SYN and SYN+ACK
            public TimeSpan latency;
            public DateTime lastTimeDetectLatency;

            // last time anything received
            public DateTime lastRead;

            // last time anything sent
            public DateTime lastWrite;

            // connection refused or closed before anything received
            public DateTime lastFailure;

            public Server server;

            public int times;
            /// <summary>
            /// 错误次数
            /// </summary>
            public int failureTimes { get; set; }

            public double score;
        }

        public HighAvailabilityStrategy(ShadowsocksController controller)
        {
            _controller = controller;
            _random = new Random();
            _serverStatus = new Dictionary<Server, ServerStatus>();
        }

        public string Name
        {
            get { return I18N.GetString("High Availability"); }
        }

        public string ID
        {
            get { return "com.shadowsocks.strategy.ha"; }
        }

        public void ReloadServers()
        {
            // make a copy to avoid locking
            var newServerStatus = new Dictionary<Server, ServerStatus>(_serverStatus);

            foreach (var server in _controller.GetCurrentConfiguration().configs)
            {
                if (!newServerStatus.ContainsKey(server))
                {
                    var status = new ServerStatus();
                    status.server = server;
                    status.lastFailure = DateTime.MinValue;
                    status.lastRead = DateTime.Now;
                    status.lastWrite = DateTime.Now;
                    status.latency = new TimeSpan(0, 0, 0, 0, 10);
                    status.lastTimeDetectLatency = DateTime.Now;
                    newServerStatus[server] = status;
                }
                else
                {
                    // update settings for existing server
                    newServerStatus[server].server = server;
                }
            }
            _serverStatus = newServerStatus;

            ChooseNewServer(IStrategyCallerType.TCP);
        }

        public Server GetAServer(IStrategyCallerType type, System.Net.IPEndPoint localIPEndPoint, EndPoint destEndPoint)
        {
            if (type == IStrategyCallerType.TCP)
            {
                ChooseNewServer(type) ;
            }
            if (_currentServer == null)
            {
                return null;
            }
            return _currentServer.server;
        }

        /**
         * once failed, try after 5 min
         * and (last write - last read) < 5s
         * and (now - last read) <  5s  // means not stuck
         * and latency < 200ms, try after 30s
         */
        public void ChooseNewServer(IStrategyCallerType type)
        {
            ServerStatus oldServer = _currentServer;
            List<ServerStatus> servers = new List<ServerStatus>(_serverStatus.Values);
            DateTime now = DateTime.Now;
            foreach (var status in servers)
            {
                // all of failure, latency, (lastread - lastwrite) normalized to 1000, then
                // 100 * failure - 2 * latency - 0.5 * (lastread - lastwrite)
                status.score =
                    100 * 1000 * Math.Min(5 * 60, (now - status.lastFailure).TotalSeconds)
                    - 2 * 5 * (Math.Min(2000, status.latency.TotalMilliseconds) / (1 + (now - status.lastTimeDetectLatency).TotalSeconds / 30 / 10) +
                    -0.5 * 200 * Math.Min(5, (status.lastRead - status.lastWrite).TotalSeconds));
                logger.Debug(String.Format("server: {0} latency:{1} score: {2}", status.server.ToString(), status.latency, status.score));
            }
            var totalCount = servers.Count;
            var minCount = Convert.ToInt32(totalCount * 0.5);
            var query = servers.Where(r => r.failureTimes <= 1);
           // query = query.Where(r => (r.lastRead - r.lastWrite).TotalMilliseconds < 5000);

            //if (servers.Any(r => r.score > 0&&r.times>3))//如果全部运行3次按评分倒序去70%
            //{
            //    var takeCount = Convert.ToInt32(servers.Count * 0.7);
            //    var configs = servers.OrderByDescending(r => r.score).Take(takeCount).ToList();
            //    int index = _random.Next();
            //    _currentServer = configs[index % configs.Count];
            //    logger.Info($"按评分负载.{_currentServer.server.ToString()}");
            //}
            //else
            //{
            var configs = query.ToList();
            if (configs.Count < minCount)
            {
                configs = servers.OrderBy(r => r.failureTimes).Take(minCount).ToList();
            }
            int index = _random.Next();
            _currentServer = configs[index % configs.Count];
            var latency = Convert.ToInt32((_currentServer.lastRead - _currentServer.lastWrite).TotalMilliseconds);
            _currentServer.times++;
            _currentServer.latency = new TimeSpan(0,0,0,0, latency);


            logger.Info($"HA switching to server: {_currentServer.server.ToString()},times:{_currentServer.times},count:{configs.Count}");
            //}

            //ServerStatus max = null;
            //foreach (var status in servers)
            //{
            //    if (max == null)
            //    {
            //        max = status;
            //    }
            //    else
            //    {
            //        if (status.score >= max.score)
            //        {
            //            max = status;
            //        }
            //    }
            //}
            //if (max != null)
            //{
            //    if (_currentServer == null || max.score - _currentServer.score > 200)
            //    {
            //        _currentServer = max;
            //        logger.Info($"HA switching to server: {_currentServer.server.ToString()}");
            //    }
            //}
        }

        public void UpdateLatency(Model.Server server, TimeSpan latency)
        {
            logger.Debug($"latency: {server.ToString()} {latency}");

            ServerStatus status;
            if (_serverStatus.TryGetValue(server, out status))
            {
                status.latency = latency;
                status.lastTimeDetectLatency = DateTime.Now;
                status.times++;
            }
        }

        public void UpdateLastRead(Model.Server server)
        {
            logger.Debug($"last read: {server.ToString()}");

            ServerStatus status;
            if (_serverStatus.TryGetValue(server, out status))
            {
                status.lastRead = DateTime.Now;
            }
        }

        public void UpdateLastWrite(Model.Server server)
        {
            logger.Debug($"last write: {server.ToString()}");

            ServerStatus status;
            if (_serverStatus.TryGetValue(server, out status))
            {
                status.lastWrite = DateTime.Now;
            }
        }

        public void SetFailure(Model.Server server)
        {
            logger.Debug($"failure: {server.ToString()}");

            ServerStatus status;
            if (_serverStatus.TryGetValue(server, out status))
            {
                status.lastFailure = DateTime.Now;
                status.failureTimes++;
            }
            
        }
    }
}
