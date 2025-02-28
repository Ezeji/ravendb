﻿using System.Linq;
using FastTests;
using Lucene.Net.Analysis;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Spatial.Queries;
using Lucene.Net.Store;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Indexes.Spatial;
using Raven.Server.Documents.Indexes.Static.Spatial;
using Spatial4n.Context.Nts;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Tests.Spatial
{
    public class SimonBartlett : RavenTestBase
    {
        public SimonBartlett(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void PureLucene()
        {
            using (var dir = new RAMDirectory())
            {
                using (var keywordAnalyzer = new KeywordAnalyzer())
                using (var writer = new IndexWriter(dir, keywordAnalyzer, true, IndexWriter.MaxFieldLength.UNLIMITED, null))
                {
                    var doc = new Lucene.Net.Documents.Document();

#pragma warning disable 612
                    var writeShape = NtsSpatialContext.Geo.ReadShape("LINESTRING (0 0, 1 1, 2 1)");
#pragma warning restore 612
                    var spatialField = new SpatialField("WKT", new SpatialOptionsFactory().Geography.Default());
                    var writeStrategy = spatialField.Strategy;
                    foreach (var f in writeStrategy.CreateIndexableFields(writeShape))
                    {
                        doc.Add(f);
                    }
                    writer.AddDocument(doc, null);
                    writer.Commit(null);
                }

#pragma warning disable 612
                var shape = NtsSpatialContext.Geo.ReadShape("LINESTRING (1 0, 1 1, 1 2)");
#pragma warning restore 612
                SpatialArgs args = new SpatialArgs(SpatialOperation.Intersects, shape);
                var spatialField2 = new SpatialField("WKT", new SpatialOptionsFactory().Geography.Default());
                var writeStrategy2 = spatialField2.Strategy;
                var makeQuery = writeStrategy2.MakeQuery(args);
                using (var search = new IndexSearcher(dir, null))
                {
                    var topDocs = search.Search(makeQuery, 5, null);
                    Assert.Equal(1, topDocs.TotalHits);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Spatial)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Lucene, Skip = "Corax is not supporting area indexing.")]
        public void LineStringsShouldIntersect(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                store.Initialize();
                store.ExecuteIndex(new GeoIndex());

                using (var session = store.OpenSession())
                {
                    session.Store(new GeoDocument { WKT = "LINESTRING (0 0, 1 1, 2 1)" });
                    session.SaveChanges();
                }

                Indexes.WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var matches = session.Query<dynamic, GeoIndex>()
                        .Spatial("WKT", factory => factory.RelatesToShape("LINESTRING (1 0, 1 1, 1 2)", SpatialRelation.Intersects))
                        .Customize(x =>
                        {
                            x.WaitForNonStaleResults();
                        }).Any();

                    Assert.True(matches);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Spatial)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Lucene)]
        public void CirclesShouldNotIntersect(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                store.Initialize();
                store.ExecuteIndex(new GeoIndex());

                using (var session = store.OpenSession())
                {
                    // 110km is approximately 1 degree
                    session.Store(new GeoDocument { WKT = "CIRCLE(0.000000 0.000000 d=110)" });
                    session.SaveChanges();
                }

                Indexes.WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var matches = session.Query<dynamic, GeoIndex>()
                        // Should not intersect, as there is 1 Degree between the two shapes
                        .Spatial("WKT", factory => factory.RelatesToShape("CIRCLE(0.000000 3.000000 d=110)", SpatialRelation.Intersects))
                        .Customize(x =>
                        {
                            x.WaitForNonStaleResults();
                        }).Any();

                    Assert.False(matches);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Spatial)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Lucene)]
        public void GeohashIndexDefaultLevel_LineStringShouldNotThrowException(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                store.Initialize();
                store.ExecuteIndex(new GeoIndex());
                ;

                using (var session = store.OpenSession())
                {
                    session.Store(new GeoDocument { WKT = "LINESTRING (0 0, 1 1, 2 1)" });
                    session.SaveChanges();
                }

                Indexes.WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var matches1 = session.Query<dynamic, GeoIndex>()
                        .Spatial("WKT", factory => factory.RelatesToShape("LINESTRING (-0.20854 51.80315, -0.20811 51.80395, -0.20811 51.80402, -0.20814 51.80407, -0.20823 51.80419, -0.20888 51.80435, -0.20978 51.80455, -0.21033 51.80463, -0.21088 51.80467, -0.2116 51.80463, -0.21199 51.80457, -0.21246 51.80453, -0.2131 51.80448, -0.21351 51.80442, -0.2143 51.80433, -0.21436 51.80372, -0.21454 51.80321, -0.21468 51.80295, -0.21495 51.80263, -0.21514 51.80245, -0.21537 51.80229, -0.21588 51.80201, -0.21694 51.80148, -0.21859 51.80068, -0.21931 51.80031, -0.22009 51.79985, -0.22049 51.79951, -0.22098 51.79907, -0.22172 51.7987, -0.22292 51.79813, -0.22386 51.79768, -0.2243 51.79734, -0.22461 51.79698, -0.22484 51.79682, -0.22479 51.79677, -0.22476 51.79673, -0.22476 51.79667, -0.22479 51.79658, -0.22487 51.79649, -0.22498 51.79644, -0.22512 51.79643, -0.22531 51.79647, -0.22544 51.79654, -0.2255 51.79661, -0.22586 51.79657, -0.22617 51.79653, -0.22674 51.79648, -0.22701 51.79645, -0.22701 51.79639, -0.22708 51.79626, -0.22719 51.79617, -0.22729 51.79616, -0.22751 51.79618, -0.2276 51.7962, -0.22769 51.79626, -0.22774 51.79634, -0.2277 51.7965, -0.22756 51.79658, -0.22741 51.7968, -0.22734 51.79696, -0.22729 51.79716, -0.22747 51.79858, -0.22756 51.79914, -0.22793 51.80076, -0.22808 51.80151, -0.22799 51.80175, -0.22859 51.80369, -0.22875 51.80451, -0.22882 51.80518, -0.22884 51.80595, -0.22879 51.80653, -0.22872 51.80714, -0.22861 51.8076, -0.2281 51.80932, -0.22763 51.81066, -0.22746 51.81108, -0.22689 51.81246, -0.2267 51.81287, -0.22651 51.8132, -0.22605 51.81382, -0.22538 51.81452, -0.22473 51.81509, -0.22429 51.81543, -0.22389 51.81573, -0.22294 51.81634, -0.22194 51.817, -0.22076 51.81778, -0.21893 51.81896, -0.21661 51.82048, -0.21502 51.82153, -0.2146 51.8218, -0.21374 51.82243, -0.21285 51.82318, -0.21229 51.82371, -0.21184 51.82421, -0.21139 51.82476, -0.21114 51.82511, -0.21054 51.82607, -0.21013 51.82698, -0.20991 51.82764, -0.20953 51.82916, -0.20925 51.83036, -0.20898 51.83145, -0.2089 51.83174, -0.2086 51.83248, -0.20824 51.8335, -0.20803 51.83403, -0.20783 51.83447, -0.20758 51.83491, -0.20716 51.83566, -0.20667 51.83645, -0.20604 51.83737, -0.20527 51.83844, -0.20455 51.83939, -0.20396 51.84017, -0.20356 51.84074, -0.20247 51.84225, -0.20118 51.84397, -0.20074 51.84453, -0.20006 51.84544, -0.19859 51.84742, -0.19785 51.84844, -0.19748 51.84903, -0.1972 51.84954, -0.19691 51.85016, -0.19652 51.85107, -0.19629 51.85185, -0.19612 51.85256, -0.19604 51.85312, -0.19599 51.85349, -0.19595 51.8541, -0.19595 51.85483, -0.19607 51.85598, -0.19625 51.85771, -0.19633 51.85839, -0.19637 51.85897, -0.19644 51.85986, -0.19657 51.86106, -0.1968 51.8635, -0.1969 51.86448, -0.19713 51.86586, -0.19725 51.86645, -0.19735 51.86679, -0.19759 51.86751, -0.19804 51.8686, -0.19847 51.8696, -0.19908 51.871, -0.19987 51.87278, -0.20085 51.87509, -0.20137 51.87625, -0.20188 51.87737, -0.2028 51.87944, -0.20368 51.88148, -0.20514 51.88471, -0.20535 51.88518, -0.20569 51.88571, -0.20623 51.88656, -0.20644 51.88684, -0.20663 51.88699, -0.20683 51.88707, -0.20713 51.88719, -0.2073 51.88735, -0.20735 51.8875, -0.20731 51.8877, -0.20723 51.88785, -0.20714 51.88792, -0.20686 51.88804, -0.20642 51.88814, -0.20612 51.88816, -0.20593 51.88815, -0.20566 51.88807, -0.20553 51.88801, -0.20532 51.88798, -0.205 51.88795, -0.20474 51.88798, -0.20328 51.88828, -0.20216 51.88853, -0.20185 51.88861, -0.20184 51.88868, -0.20177 51.88875, -0.20161 51.88881, -0.20142 51.88882, -0.20133 51.88881, -0.20127 51.8888, -0.20092 51.88886, -0.19987 51.88919, -0.1994 51.88935, -0.19815 51.8898, -0.19549 51.89073, -0.19549 51.89077, -0.19547 51.89089, -0.19548 51.89108, -0.19552 51.89128, -0.19596 51.89198, -0.19635 51.89259, -0.19712 51.89375, -0.19755 51.89442, -0.19812 51.89541, -0.19821 51.89558, -0.19846 51.89641, -0.19858 51.89731, -0.19859 51.89755, -0.19863 51.89846, -0.19871 51.89889, -0.19875 51.89896, -0.19883 51.89907, -0.19889 51.89908, -0.19897 51.8991, -0.19902 51.89914, -0.19911 51.89923, -0.19913 51.89932, -0.19912 51.89939, -0.19907 51.89946, -0.19898 51.89953, -0.19889 51.89955, -0.19888 51.89987, -0.19895 51.90029, -0.19905 51.90095, -0.1991 51.90117, -0.19949 51.90247, -0.1998 51.90335, -0.20003 51.90384, -0.20005 51.90389, -0.20026 51.90448, -0.20035 51.90466, -0.20048 51.90483, -0.20062 51.90495, -0.20078 51.90503, -0.20089 51.90508, -0.20095 51.90512, -0.20098 51.9052, -0.20099 51.9053, -0.20092 51.9054, -0.20072 51.90545, -0.20039 51.90548, -0.20026 51.90547, -0.20016 51.90542, -0.20008 51.90533, -0.20007 51.9052, -0.2002 51.90507, -0.20027 51.90491, -0.20026 51.9047, -0.19981 51.90364, -0.19947 51.90295, -0.19917 51.90201, -0.19864 51.90202, -0.19828 51.90199, -0.19728 51.9018, -0.19713 51.90238, -0.19698 51.90282, -0.19689 51.90299, -0.19663 51.90376)", SpatialRelation.Intersects))
                        .Customize(x =>
                        {
                            x.WaitForNonStaleResults();
                        }).Any();

                    Assert.False(matches1);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Spatial)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Lucene)]
        public void GeohashIndexLevel7_LineStringShouldNotThrowException(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                store.Initialize();
                store.ExecuteIndex(new GeohashIndexLevel7());
                ;

                using (var session = store.OpenSession())
                {
                    session.Store(new GeoDocument { WKT = "LINESTRING (0 0, 1 1, 2 1)" });
                    session.SaveChanges();
                }

                Indexes.WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var matches1 = session.Query<dynamic, GeohashIndexLevel7>()
                        .Spatial("WKT", factory => factory.RelatesToShape("LINESTRING (-0.20854 51.80315, -0.20811 51.80395, -0.20811 51.80402, -0.20814 51.80407, -0.20823 51.80419, -0.20888 51.80435, -0.20978 51.80455, -0.21033 51.80463, -0.21088 51.80467, -0.2116 51.80463, -0.21199 51.80457, -0.21246 51.80453, -0.2131 51.80448, -0.21351 51.80442, -0.2143 51.80433, -0.21436 51.80372, -0.21454 51.80321, -0.21468 51.80295, -0.21495 51.80263, -0.21514 51.80245, -0.21537 51.80229, -0.21588 51.80201, -0.21694 51.80148, -0.21859 51.80068, -0.21931 51.80031, -0.22009 51.79985, -0.22049 51.79951, -0.22098 51.79907, -0.22172 51.7987, -0.22292 51.79813, -0.22386 51.79768, -0.2243 51.79734, -0.22461 51.79698, -0.22484 51.79682, -0.22479 51.79677, -0.22476 51.79673, -0.22476 51.79667, -0.22479 51.79658, -0.22487 51.79649, -0.22498 51.79644, -0.22512 51.79643, -0.22531 51.79647, -0.22544 51.79654, -0.2255 51.79661, -0.22586 51.79657, -0.22617 51.79653, -0.22674 51.79648, -0.22701 51.79645, -0.22701 51.79639, -0.22708 51.79626, -0.22719 51.79617, -0.22729 51.79616, -0.22751 51.79618, -0.2276 51.7962, -0.22769 51.79626, -0.22774 51.79634, -0.2277 51.7965, -0.22756 51.79658, -0.22741 51.7968, -0.22734 51.79696, -0.22729 51.79716, -0.22747 51.79858, -0.22756 51.79914, -0.22793 51.80076, -0.22808 51.80151, -0.22799 51.80175, -0.22859 51.80369, -0.22875 51.80451, -0.22882 51.80518, -0.22884 51.80595, -0.22879 51.80653, -0.22872 51.80714, -0.22861 51.8076, -0.2281 51.80932, -0.22763 51.81066, -0.22746 51.81108, -0.22689 51.81246, -0.2267 51.81287, -0.22651 51.8132, -0.22605 51.81382, -0.22538 51.81452, -0.22473 51.81509, -0.22429 51.81543, -0.22389 51.81573, -0.22294 51.81634, -0.22194 51.817, -0.22076 51.81778, -0.21893 51.81896, -0.21661 51.82048, -0.21502 51.82153, -0.2146 51.8218, -0.21374 51.82243, -0.21285 51.82318, -0.21229 51.82371, -0.21184 51.82421, -0.21139 51.82476, -0.21114 51.82511, -0.21054 51.82607, -0.21013 51.82698, -0.20991 51.82764, -0.20953 51.82916, -0.20925 51.83036, -0.20898 51.83145, -0.2089 51.83174, -0.2086 51.83248, -0.20824 51.8335, -0.20803 51.83403, -0.20783 51.83447, -0.20758 51.83491, -0.20716 51.83566, -0.20667 51.83645, -0.20604 51.83737, -0.20527 51.83844, -0.20455 51.83939, -0.20396 51.84017, -0.20356 51.84074, -0.20247 51.84225, -0.20118 51.84397, -0.20074 51.84453, -0.20006 51.84544, -0.19859 51.84742, -0.19785 51.84844, -0.19748 51.84903, -0.1972 51.84954, -0.19691 51.85016, -0.19652 51.85107, -0.19629 51.85185, -0.19612 51.85256, -0.19604 51.85312, -0.19599 51.85349, -0.19595 51.8541, -0.19595 51.85483, -0.19607 51.85598, -0.19625 51.85771, -0.19633 51.85839, -0.19637 51.85897, -0.19644 51.85986, -0.19657 51.86106, -0.1968 51.8635, -0.1969 51.86448, -0.19713 51.86586, -0.19725 51.86645, -0.19735 51.86679, -0.19759 51.86751, -0.19804 51.8686, -0.19847 51.8696, -0.19908 51.871, -0.19987 51.87278, -0.20085 51.87509, -0.20137 51.87625, -0.20188 51.87737, -0.2028 51.87944, -0.20368 51.88148, -0.20514 51.88471, -0.20535 51.88518, -0.20569 51.88571, -0.20623 51.88656, -0.20644 51.88684, -0.20663 51.88699, -0.20683 51.88707, -0.20713 51.88719, -0.2073 51.88735, -0.20735 51.8875, -0.20731 51.8877, -0.20723 51.88785, -0.20714 51.88792, -0.20686 51.88804, -0.20642 51.88814, -0.20612 51.88816, -0.20593 51.88815, -0.20566 51.88807, -0.20553 51.88801, -0.20532 51.88798, -0.205 51.88795, -0.20474 51.88798, -0.20328 51.88828, -0.20216 51.88853, -0.20185 51.88861, -0.20184 51.88868, -0.20177 51.88875, -0.20161 51.88881, -0.20142 51.88882, -0.20133 51.88881, -0.20127 51.8888, -0.20092 51.88886, -0.19987 51.88919, -0.1994 51.88935, -0.19815 51.8898, -0.19549 51.89073, -0.19549 51.89077, -0.19547 51.89089, -0.19548 51.89108, -0.19552 51.89128, -0.19596 51.89198, -0.19635 51.89259, -0.19712 51.89375, -0.19755 51.89442, -0.19812 51.89541, -0.19821 51.89558, -0.19846 51.89641, -0.19858 51.89731, -0.19859 51.89755, -0.19863 51.89846, -0.19871 51.89889, -0.19875 51.89896, -0.19883 51.89907, -0.19889 51.89908, -0.19897 51.8991, -0.19902 51.89914, -0.19911 51.89923, -0.19913 51.89932, -0.19912 51.89939, -0.19907 51.89946, -0.19898 51.89953, -0.19889 51.89955, -0.19888 51.89987, -0.19895 51.90029, -0.19905 51.90095, -0.1991 51.90117, -0.19949 51.90247, -0.1998 51.90335, -0.20003 51.90384, -0.20005 51.90389, -0.20026 51.90448, -0.20035 51.90466, -0.20048 51.90483, -0.20062 51.90495, -0.20078 51.90503, -0.20089 51.90508, -0.20095 51.90512, -0.20098 51.9052, -0.20099 51.9053, -0.20092 51.9054, -0.20072 51.90545, -0.20039 51.90548, -0.20026 51.90547, -0.20016 51.90542, -0.20008 51.90533, -0.20007 51.9052, -0.2002 51.90507, -0.20027 51.90491, -0.20026 51.9047, -0.19981 51.90364, -0.19947 51.90295, -0.19917 51.90201, -0.19864 51.90202, -0.19828 51.90199, -0.19728 51.9018, -0.19713 51.90238, -0.19698 51.90282, -0.19689 51.90299, -0.19663 51.90376)", SpatialRelation.Intersects))
                        .Customize(x =>
                        {
                            x.WaitForNonStaleResults();
                        }).Any();

                    Assert.False(matches1);
                }
            }
        }

        private class GeoDocument
        {
            public string WKT { get; set; }
        }

        private class GeoIndex : AbstractIndexCreationTask<GeoDocument>
        {
            public GeoIndex()
            {
                Map = docs => from doc in docs
                              select new { WKT = CreateSpatialField(doc.WKT) };

                SpatialIndexes.Add(x => x.WKT, new SpatialOptions
                {
                    Strategy = SpatialSearchStrategy.GeohashPrefixTree
                });
            }
        }

        private class GeohashIndexLevel7 : AbstractIndexCreationTask<GeoDocument>
        {
            public GeohashIndexLevel7()
            {
                Map = docs => from doc in docs
                              select new { WKT = CreateSpatialField(doc.WKT) };
            }
        }
    }
}
