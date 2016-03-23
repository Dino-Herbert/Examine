﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlServerCe;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using Examine.LuceneEngine;
using Examine.LuceneEngine.Faceting;
using Examine.LuceneEngine.Indexing;
using Examine.Session;
using Examine.LuceneEngine.Providers;
using Examine.LuceneEngine.Scoring;
using Examine.Web.Demo.Models;
using Examine.LuceneEngine.SearchCriteria;
using Lucene.Net.Index;
using Lucene.Net.Search;
using NLipsum.Core;

namespace Examine.Web.Demo.Controllers
{
    public class HomeController : Controller
    {


        [HttpGet]
        public ActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public ActionResult Search(string id)
        {
            var searcher = ExamineManager.Instance.GetSearcher("Simple2Indexer");
            var criteria = searcher.CreateCriteria();
            var result = searcher.Find(criteria.RawQuery(id));
            
            return View(result);
        }

        [HttpPost]
        public ActionResult Populate()
        {
            try
            {                
                using (var db = new MyDbContext())
                {
                    //check if we have data
                    if (!db.TestModels.Any())
                    {
                        var r = new Random();
                        //using TableDirect is BY FAR one of the fastest ways to bulk insert data in SqlCe... 
                        using (db.Database.Connection)
                        {
                            db.Database.Connection.Open();
                            using (var cmd = (SqlCeCommand)db.Database.Connection.CreateCommand())
                            {
                                cmd.CommandText = "TestModels";
                                cmd.CommandType = CommandType.TableDirect;

                                var rs = cmd.ExecuteResultSet(ResultSetOptions.Updatable);
                                var rec = rs.CreateRecord();
                                
                                
                                for (var i = 0; i < 27000; i++)
                                {
                                    var path = new List<string> { "Root" };
                                    for (int j = 0, n = r.Next(1, 3); j < n; j++)
                                    {
                                        path.Add("Tax" + r.Next(0, 5));
                                    }

                                    rec.SetString(1, "a" + r.Next(0, 10));
                                    rec.SetString(2, r.Next(1000, 1200).ToString());
                                    rec.SetString(3, "c" + i);
                                    rec.SetString(4, string.Join("/", path));
                                    rec.SetString(5, LipsumGenerator.GenerateHtml(r.Next(1, 5)));
                                    rec.SetString(6, "This is a nice little test. Made by Kühnel");
                                    rs.Insert(rec);
                                }
                            }
                        }
                        return View(true);
                    }
                    else
                    {
                        this.ModelState.AddModelError("DataError", "The database has already been populated with data");
                        return View(false);
                    }
                }
            }
            catch (Exception ex)
            {
                this.ModelState.AddModelError("DataError", ex.Message);
                return View(false);
            }

        }

        [HttpGet]
        public ActionResult SearchCustom(string indexName, string q = null, int count = 10, bool all = false)
        {
            var searcher = ExamineManager.Instance.GetSearcher(indexName);
            
            ILuceneSearchResults result;
            if (all)
            {
                result = searcher.Find(searcher.CreateCriteria().All().Compile());
            }
            else
            {
                result = searcher.Find(q, false);
            }

            return View("Search", result);
        }

        [HttpGet]
        public ActionResult SearchFacets(
            string q = null, int count = 10, 
            bool countFacets = true, 
            bool facetFilter = true, string facetFilterField = "Column1_Facet",
            bool all = false, 
            //TODO: Not used right now
            double likeWeight = 0)
        {
            var model = new FacetSearchModel();

            var sw = new Stopwatch();
            sw.Start();
            var searcher = ExamineManager.Instance.GetSearcher("Simple2Indexer");
            
            
            //Create a basic criteria with the options from the query string
            var criteria = searcher.CreateCriteria()
                                   .MaxCount(count)
                                   .CountFacets(countFacets)
                                   .CountFacetReferences(true);

            if (all || string.IsNullOrEmpty(q))
            {
                criteria.All();
            }
            else
            {
                if (facetFilter)
                {
                    //Add column1 filter as facet filter
                    criteria
                        .Facets(new FacetKey(facetFilterField, q))
                        .Compile();
                        //Here, zero means that we don't case about Lucene's score. We only want to know how well the results compare to the facets
                        //.WrapRelevanceScore(0, new FacetKeyLevel("Column4", "Root/Tax1/Tax2", 1));

                        //TODO: Determine if this is working as it should, I think Niels K said it might not be, can't remember
                        ////Score by the like count we have in the external in-memory data.
                        ////The value is normalized. Here we know that we can't have more than 1000 likes. 
                        ////Generally be careful about the scale of the scores you combine. 
                        ////If you compare large numbers to small numbers use a logarithmic transform on the large one (e.g. comparing likes to number of comments)
                        //.WrapExternalDataScore<TestExternalData>(new ScoreAdder(1 - likeWeight), d => d.Likes / 1000f); 
                }
                else
                {
                    //Add column1 filter as normal field query
                    //criteria.Field("Column1", q);
                    criteria.ManagedQuery(q, fields: new[] {"Column5", "Column6"})
                        //.Or().Field("Column6", "test")
                        .Compile();
                        //TODO: Determine if this is working as it should, I think Niels K said it might not be, can't remember
                        //.WrapExternalDataScore<TestExternalData>(1 - likeWeight, d => d.Likes / 1000f); 
                }
            }
            
            //Get search results
            var searchResults = searcher.Find(criteria);

            model.SearchResult = searchResults;
            model.CountFacets = countFacets;
            model.Watch = sw;
            model.FacetMap = searchResults.CriteriaContext.FacetMap;
            
            return View(model);

        }

        [HttpPost]
        public ActionResult RebuildIndex()
        {
            var timer = new Stopwatch();
            timer.Start();
            var index = ExamineManager.Instance.IndexProviders["Simple2Indexer"];
            index.RebuildIndex();
            timer.Stop();

            //rebuild our non-config index
            ExamineManager.Instance.IndexProviders["RuntimeIndexer1"].RebuildIndex();


            return View(timer.Elapsed.TotalSeconds);
        }

        [HttpPost]
        public ActionResult ReIndexEachItemIndividually()
        {
            try
            {
                var timer = new Stopwatch();
                timer.Start();
                var ds = new TableDirectReaderDataService();
                foreach (var i in ds.GetAllData("TestType"))
                {
                    ExamineManager.Instance.IndexProviders["Simple2Indexer"].IndexItems(i);                        
                }
                timer.Stop();
                
                ExamineSession.WaitForChanges();

                return View(timer.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                this.ModelState.AddModelError("DataError", ex.Message);
                return View(0d);
            }
        }


    }
}
