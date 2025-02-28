﻿using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using System.Linq;
using Raven.Client.Documents.Operations;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Bugs
{
    public class CanAggregateOnDecimal : RavenTestBase
    {
        public CanAggregateOnDecimal(ITestOutputHelper output) : base(output)
        {
        }

        public class DecimalAggregationMap : AbstractIndexCreationTask<Bank, BankTotal>
        {
            public DecimalAggregationMap()
            {
                Map = banks => from bank in banks
                               select new { Total = bank.Accounts.Sum(x => x.Amount) };
                Store(x => x.Total, FieldStorage.Yes);
            }
        }

        public class DecimalAggregationReduce : AbstractIndexCreationTask<Bank, BankTotal>
        {
            public DecimalAggregationReduce()
            {
                Map = banks => from bank in banks
                               select new { Total = bank.Accounts.Sum(x => x.Amount) };
                Reduce = results => from bankTotal in results
                                    group bankTotal by 1
                                    into g
                                    select new { Total = g.Sum(x => x.Total) };
            }
        }

        public class BankTotal
        {
            public decimal Total { get; set; }
        }

        public class Bank
        {
            public Account[] Accounts { get; set; }
        }
        public class Account
        {
            public decimal Amount { get; set; }
        }

        [Theory]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
        public void MapOnly(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                new DecimalAggregationMap().Execute(store);
                using (var session = store.OpenSession())
                {
                    session.Store(new Bank
                    {
                        Accounts = new[]
                        {
                            new Account {Amount = 0.1m},
                            new Account {Amount = 321.312m},
                        }
                    });
                    session.SaveChanges();
                }
                Indexes.WaitForIndexing(store);
                var stats = store.Maintenance.Send(new GetStatisticsOperation());
                Assert.False(stats.Indexes.Any(i => i.State == IndexState.Error));
                using (var session = store.OpenSession())
                {
                    var bankTotal = session.Query<BankTotal, DecimalAggregationMap>()
                        .Customize(x => x.WaitForNonStaleResults())
                        .ProjectInto<BankTotal>()
                        .Single();

                    Assert.Equal(321.412m, bankTotal.Total);
                }
            }
        }

        [Theory]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
        public void Reduce(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                new DecimalAggregationReduce().Execute(store);
                using (var session = store.OpenSession())
                {
                    for (int i = 0; i < 5; i++)
                    {
                        session.Store(new Bank
                        {
                            Accounts = new[]
                        {
                            new Account {Amount = 0.1m},
                            new Account {Amount = 321.312m},
                        }
                        });
                    }
                    session.SaveChanges();
                }
                Indexes.WaitForIndexing(store);
                var stats = store.Maintenance.Send(new GetStatisticsOperation());
                Assert.False(stats.Indexes.Any(i => i.State == IndexState.Error));

                using (var session = store.OpenSession())
                {
                    var bankTotal = session.Query<BankTotal, DecimalAggregationReduce>()
                        .Customize(x => x.WaitForNonStaleResults()).Single();

                    Assert.Equal(1607.060m, bankTotal.Total);
                }
            }
        }
    }
}
