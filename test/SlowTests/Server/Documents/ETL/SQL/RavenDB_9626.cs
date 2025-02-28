﻿using System.Collections.Generic;
using FastTests;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.ETL.SQL;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.ETL.SQL
{
    public class RavenDB_9626 : NoDisposalNeeded
    {
        public RavenDB_9626(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void Error_if_script_does_not_contain_any_loadTo_method_and_uses_legacy_replicateTo()
        {
            var config = new SqlEtlConfiguration
            {
                Name = "test",
                ConnectionStringName = "test",
                Transforms =
                {
                    new Transformation
                    {
                        Name = "test",
                        Collections = {"Users"},
                        Script = @"this.Name = 'aaa'; replicateToUsers(this);"
                    }
                },
                SqlTables =
                {
                    new SqlEtlTable {TableName = "Orders", DocumentIdColumn = "Id", InsertOnlyMode = false},
                    new SqlEtlTable {TableName = "OrderLines", DocumentIdColumn = "OrderId", InsertOnlyMode = false},
                }
            };

            config.Initialize(new SqlConnectionString { ConnectionString = @"Data Source=localhost\sqlexpress", FactoryName = "System.Data.SqlClient"});

            List<string> errors;
            config.Validate(out errors);

            Assert.Equal(2, errors.Count);

            Assert.Equal("No `loadTo<TableName>()` method call found in 'test' script", errors[1]);
            Assert.Equal("Found `replicateTo<TableName>()` method in 'test' script which is not supported. " +
                         "If you are using the SQL replication script from RavenDB 3.x version then please use `loadTo<TableName>()` instead.", errors[0]);

        }

        [Fact]
        public void Error_if_script_is_empty()
        {
            var config = new SqlEtlConfiguration
            {
                Name = "test",
                ConnectionStringName = "test",
                Transforms =
                {
                    new Transformation
                    {
                        Name = "test",
                        Collections = {"Users"},
                        Script = @""
                    }
                },
                SqlTables =
                {
                    new SqlEtlTable {TableName = "Orders", DocumentIdColumn = "Id", InsertOnlyMode = false},
                    new SqlEtlTable {TableName = "OrderLines", DocumentIdColumn = "OrderId", InsertOnlyMode = false},
                }
            };

            config.Initialize(new SqlConnectionString { ConnectionString = @"Data Source=localhost\sqlexpress", FactoryName = "System.Data.SqlClient"});

            List<string> errors;
            config.Validate(out errors);

            Assert.Equal(1, errors.Count);

            Assert.Equal("Script 'test' must not be empty", errors[0]);

        }
    }
}
