﻿// Copyright (c) Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeviceBridge.Models;
using DeviceBridge.Services;
using Microsoft.QualityTools.Testing.Fakes;
using Moq;
using NLog;
using NUnit.Framework;

namespace DeviceBridge.Providers.Tests
{
    [TestFixture]
    public class StorageProviderTests
    {
        private Mock<IEncryptionService> _encriptionServiceMock;
        private StorageProvider _storageProvider;

        [SetUp]
        public async Task Setup()
        {
            _encriptionServiceMock = new Mock<IEncryptionService>();
            _encriptionServiceMock.Setup(p => p.Encrypt(It.IsAny<Logger>(), It.IsAny<string>())).Returns((Logger _, string s) => Task.FromResult(s));
            _encriptionServiceMock.Setup(p => p.Decrypt(It.IsAny<Logger>(), It.IsAny<string>())).Returns((Logger _, string s) => Task.FromResult(s));
            _storageProvider = new StorageProvider("", _encriptionServiceMock.Object);
        }

        [Test]
        [Description("Continuously calls getDeviceSubscriptionsPaged stored procedure to return all subscriptions in the DB")]
        public async Task ListAllSubscriptionsOrderedByDeviceId()
        {
            using (ShimsContext.Create())
            {
                _encriptionServiceMock.Invocations.Clear();

                // Return 55 subscriptions.
                var testDateTime = DateTime.Now;
                var testSub = GetTestSubscription(testDateTime);
                var allSubs = Enumerable.Repeat(testSub, 55).ToList();

                List<Dictionary<string, object>> currentPage = null;
                Dictionary<string, object> currentSub = null;

                ShimOpen();

                // Get the next page of 10 items when ExecuteReaderAsync is called.
                ShimExecuteReader("getDeviceSubscriptionsPaged", null, cmd =>
                {
                    var nextPageSize = allSubs.Count < 10 ? allSubs.Count : 10;
                    currentPage = allSubs.Take(nextPageSize).ToList();
                    allSubs.RemoveRange(0, nextPageSize);
                    Assert.AreEqual(CommandType.StoredProcedure, cmd.CommandType);
                });

                // Get the next item when ReadAsync is called.
                ShimRead(() =>
                {
                    if (currentPage.Count > 0)
                    {
                        currentSub = currentPage[0];
                        currentPage.RemoveAt(0);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                });

                ShimItemGetString(() => currentSub);

                var result = await _storageProvider.ListAllSubscriptionsOrderedByDeviceId(LogManager.GetCurrentClassLogger());
                Assert.AreEqual(55, result.FindAll(s => s.DeviceId == "test-device" && s.CallbackUrl == "http://test" && s.SubscriptionType == DeviceSubscriptionType.DesiredProperties && s.CreatedAt == testDateTime).Count);
                _encriptionServiceMock.Verify(p => p.Decrypt(It.IsAny<Logger>(), It.IsAny<string>()), Times.Exactly(55));
            }
        }

        [Test]
        [Description("Executes a select to get subscriptions of all types for the device Id received as parameter")]
        public async Task ListDeviceSubscriptions()
        {
            using (ShimsContext.Create())
            {
                _encriptionServiceMock.Invocations.Clear();
                ShimOpen();
                ShimExecuteReader("SELECT * FROM DeviceSubscriptions WHERE DeviceId = @DeviceId", new Dictionary<string, string>() { { "DeviceId", "test-device" } });
                ShimRead(1);

                var testDateTime = DateTime.Now;
                ShimItemGetString(GetTestSubscription(testDateTime));

                var result = await _storageProvider.ListDeviceSubscriptions(LogManager.GetCurrentClassLogger(), "test-device");
                Assert.AreEqual(1, result.Count);
                Assert.True(result[0].DeviceId == "test-device" && result[0].CallbackUrl == "http://test" && result[0].SubscriptionType == DeviceSubscriptionType.DesiredProperties && result[0].CreatedAt == testDateTime);
                _encriptionServiceMock.Verify(p => p.Decrypt(It.IsAny<Logger>(), "http://test"), Times.Once());
            }
        }

        [Test]
        [Description("Executes a select to get the subscription for the type and device Id received as parameter")]
        public async Task GetDeviceSubscription()
        {
            using (ShimsContext.Create())
            {
                _encriptionServiceMock.Invocations.Clear();
                ShimOpen();
                ShimExecuteReader("SELECT * FROM DeviceSubscriptions WHERE DeviceId = @DeviceId AND SubscriptionType = @SubscriptionType", new Dictionary<string, string>() { { "DeviceId", "test-device" }, { "SubscriptionType", "DesiredProperties" } });
                ShimRead(1);

                var testDateTime = DateTime.Now;
                ShimItemGetString(GetTestSubscription(testDateTime));

                var result = await _storageProvider.GetDeviceSubscription(LogManager.GetCurrentClassLogger(), "test-device", DeviceSubscriptionType.DesiredProperties, default);
                Assert.True(result.DeviceId == "test-device" && result.CallbackUrl == "http://test" && result.SubscriptionType == DeviceSubscriptionType.DesiredProperties && result.CreatedAt == testDateTime);
                _encriptionServiceMock.Verify(p => p.Decrypt(It.IsAny<Logger>(), "http://test"), Times.Once());
            }
        }

        [Test]
        [Description("Calls upsertDeviceSubscription to create a subscription of the given type, device Id, and callback URL")]
        public async Task CreateOrUpdateDeviceSubscription()
        {
            using (ShimsContext.Create())
            {
                _encriptionServiceMock.Invocations.Clear();
                ShimOpen();

                var testDateTime = DateTime.Now;
                ShimExecuteNonQuery("upsertDeviceSubscription", new Dictionary<string, string>() { { "@DeviceId", "test-device" }, { "@SubscriptionType", "DesiredProperties" }, { "@CallbackUrl", "http://test" } }, cmd =>
                {
                    Assert.AreEqual(CommandType.StoredProcedure, cmd.CommandType);
                    cmd.Parameters.RemoveAt("@CreatedAt");
                    cmd.Parameters.Add(new SqlParameter("@CreatedAt", testDateTime));
                });

                var result = await _storageProvider.CreateOrUpdateDeviceSubscription(LogManager.GetCurrentClassLogger(), "test-device", DeviceSubscriptionType.DesiredProperties, "http://test", default);
                Assert.True(result.DeviceId == "test-device" && result.CallbackUrl == "http://test" && result.SubscriptionType == DeviceSubscriptionType.DesiredProperties && result.CreatedAt == testDateTime);
                _encriptionServiceMock.Verify(p => p.Encrypt(It.IsAny<Logger>(), "http://test"), Times.Once());
            }
        }


        [Test]
        [Description("Executes a delete to remove a single subscription of a given type for the given device Id")]
        public async Task DeleteDeviceSubscription()
        {
            using (ShimsContext.Create())
            {
                ShimOpen();
                ShimExecuteNonQuery("DELETE FROM DeviceSubscriptions WHERE DeviceId = @DeviceId AND SubscriptionType = @SubscriptionType", new Dictionary<string, string>() { { "DeviceId", "test-device" }, { "SubscriptionType", "DesiredProperties" } });
                await _storageProvider.DeleteDeviceSubscription(LogManager.GetCurrentClassLogger(), "test-device", DeviceSubscriptionType.DesiredProperties, default);
            }
        }

        [Test]
        [Description("Executes a filtered delete to remove all subscriptions older than a 7 days")]
        public async Task GcHubCache()
        {
            using (ShimsContext.Create())
            {
                ShimOpen();
                ShimExecuteNonQuery(null, null, cmd =>
                {
                    var expected = @"DELETE c FROM HubCache c
                                    LEFT JOIN DeviceSubscriptions s ON s.DeviceId = c.DeviceId
                                    WHERE (s.DeviceId IS NULL) AND (c.RenewedAt < DATEADD(day, -7, GETUTCDATE()))";
                    Assert.AreEqual(Regex.Replace(expected, @"\s+", " "), Regex.Replace(cmd.CommandText, @"\s+", " "));
                });
                await _storageProvider.GcHubCache(LogManager.GetCurrentClassLogger());
            }
        }

        [Test]
        [Description("Uses a bulk copy and update to renew the timestamp field of all device Ids received as parameter")]
        public async Task RenewHubCacheEntries()
        {
            using (ShimsContext.Create())
            {
                ShimOpen();
                ShimExecuteNonQuery("CREATE TABLE #CacheEntriesToRenewTmpTable(DeviceId VARCHAR(255) NOT NULL PRIMARY KEY)");

                // Once data is sent to server, check that we remove the temp table.
                System.Data.SqlClient.Fakes.ShimSqlBulkCopy.AllInstances.WriteToServerAsyncDataTable = (bulkCopy, dt) =>
                {
                    Assert.AreEqual(60, bulkCopy.BulkCopyTimeout);
                    Assert.AreEqual(1000, bulkCopy.BatchSize);
                    Assert.AreEqual("#CacheEntriesToRenewTmpTable", bulkCopy.DestinationTableName);

                    Assert.AreEqual("test-device-1", dt.Rows[0]["DeviceId"]);
                    Assert.AreEqual("test-device-2", dt.Rows[1]["DeviceId"]);
                    Assert.AreEqual("test-device-3", dt.Rows[2]["DeviceId"]);

                    ShimExecuteNonQuery(null, null, cmd =>
                    {
                        var expected = @"UPDATE HubCache SET RenewedAt = GETUTCDATE()
                                        FROM HubCache
                                        INNER JOIN #CacheEntriesToRenewTmpTable Temp ON (Temp.DeviceId = HubCache.DeviceId)
                                        DROP TABLE #CacheEntriesToRenewTmpTable";
                        Assert.AreEqual(Regex.Replace(expected, @"\s+", " "), Regex.Replace(cmd.CommandText, @"\s+", " "));
                        Assert.AreEqual(300, cmd.CommandTimeout);
                    });

                    return Task.CompletedTask;
                };

                await _storageProvider.RenewHubCacheEntries(LogManager.GetCurrentClassLogger(), new List<string>() { "test-device-1", "test-device-2", "test-device-3" });
            }
        }

        [Test]
        [Description("Calls upsertHubCacheEntry to insert a hub cache entry for a single device")]
        public async Task AddOrUpdateHubCacheEntry()
        {
            using (ShimsContext.Create())
            {
                ShimOpen();

                ShimExecuteNonQuery("upsertHubCacheEntry", new Dictionary<string, string>() { { "@DeviceId", "test-device" }, { "@Hub", "test-hub" } }, cmd =>
                {
                    Assert.AreEqual(CommandType.StoredProcedure, cmd.CommandType);
                });

                await _storageProvider.AddOrUpdateHubCacheEntry(LogManager.GetCurrentClassLogger(), "test-device", "test-hub");
            }
        }

        [Test]
        [Description("Continuously calls getHubCacheEntriesPaged to get all entries from the HubCache table")]
        public async Task ListHubCacheEntries()
        {
            using (ShimsContext.Create())
            {
                // Return 55 hubs.
                var testSub = new Dictionary<string, object>()
                {
                    { "DeviceId",  "test-device" },
                    { "Hub",  "test-hub" },
                };
                var allHubs = Enumerable.Repeat(testSub, 55).ToList();

                List<Dictionary<string, object>> currentPage = null;
                Dictionary<string, object> currentHub = null;

                ShimOpen();

                // Get the next page of 10 items when ExecuteReaderAsync is called.
                ShimExecuteReader("getHubCacheEntriesPaged", null, cmd =>
                {
                    var nextPageSize = allHubs.Count < 10 ? allHubs.Count : 10;
                    currentPage = allHubs.Take(nextPageSize).ToList();
                    allHubs.RemoveRange(0, nextPageSize);
                    Assert.AreEqual(CommandType.StoredProcedure, cmd.CommandType);
                });

                // Get the next item when ReadAsync is called.
                ShimRead(() =>
                {
                    if (currentPage.Count > 0)
                    {
                        currentHub = currentPage[0];
                        currentPage.RemoveAt(0);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                });

                ShimItemGetString(() => currentHub);

                var result = await _storageProvider.ListHubCacheEntries(LogManager.GetCurrentClassLogger());
                Assert.AreEqual(55, result.FindAll(s => s.DeviceId == "test-device" && s.Hub == "test-hub").Count);
            }
        }

        [Test]
        [Description("Executes an arbitrary SQL statement in a connection")]
        public async Task Exec()
        {
            using (ShimsContext.Create())
            {
                ShimOpen();
                ShimExecuteNonQuery("my test query");
                await _storageProvider.Exec(LogManager.GetCurrentClassLogger(), "my test query");
            }
        }

        private static void ShimOpen()
        {
            System.Data.SqlClient.Fakes.ShimSqlConnection.AllInstances.OpenAsyncCancellationToken = (_, __) => Task.CompletedTask;
        }

        private static void ShimExecuteReader(string cmdText, Dictionary<string, string> parameters = null, Action<SqlCommand> onExecute = null)
        {
            Func<SqlCommand, Task<SqlDataReader>> shim = (SqlCommand cmd) =>
            {
                Assert.AreEqual(cmdText, cmd.CommandText);

                if (parameters != null)
                {
                    foreach (var entry in parameters)
                    {
                        Assert.AreEqual(entry.Value, cmd.Parameters[entry.Key].Value);
                    }
                }

                if (onExecute != null)
                {
                    onExecute(cmd);
                }

                return Task.FromResult<SqlDataReader>(new System.Data.SqlClient.Fakes.ShimSqlDataReader());
            };

            System.Data.SqlClient.Fakes.ShimSqlCommand.AllInstances.ExecuteReaderAsync = cmd => shim(cmd);
            System.Data.SqlClient.Fakes.ShimSqlCommand.AllInstances.ExecuteReaderAsyncCancellationToken = (cmd, _) => shim(cmd);
        }

        private static void ShimRead(int times)
        {
            System.Data.SqlClient.Fakes.ShimSqlDataReader.AllInstances.ReadAsyncCancellationToken = (_, __) => Task.FromResult(times-- > 0);
        }

        private static void ShimRead(Func<bool> onRead)
        {
            System.Data.SqlClient.Fakes.ShimSqlDataReader.AllInstances.ReadAsyncCancellationToken = (_, __) => Task.FromResult(onRead());
        }

        private static void ShimItemGetString(Dictionary<string, object> item)
        {
            System.Data.SqlClient.Fakes.ShimSqlDataReader.AllInstances.ItemGetString = (_, name) => item[name];
        }

        private static void ShimItemGetString(Func<Dictionary<string, object>> getItem)
        {
            System.Data.SqlClient.Fakes.ShimSqlDataReader.AllInstances.ItemGetString = (_, name) => getItem()[name];
        }

        private static void ShimExecuteNonQuery(string cmdText = null, Dictionary<string, string> parameters = null, Action<SqlCommand> onExecute = null)
        {
            System.Data.SqlClient.Fakes.ShimSqlCommand.AllInstances.ExecuteNonQueryAsyncCancellationToken = (cmd, __) =>
            {
                if (cmdText != null)
                {
                    Assert.AreEqual(cmdText, cmd.CommandText);
                }

                if (parameters != null)
                {
                    foreach (var entry in parameters)
                    {
                        Assert.AreEqual(entry.Value, cmd.Parameters[entry.Key].Value);
                    }
                }

                if (onExecute != null)
                {
                    onExecute(cmd);
                }

                return Task.FromResult(1);
            };
        }

        private static Dictionary<string, object> GetTestSubscription(DateTime createdAt)
        {
            return new Dictionary<string, object>()
            {
                { "DeviceId",  "test-device" },
                { "SubscriptionType",  "DesiredProperties" },
                { "CallbackUrl",  "http://test" },
                { "CreatedAt", createdAt },
            };
        }
    }
}