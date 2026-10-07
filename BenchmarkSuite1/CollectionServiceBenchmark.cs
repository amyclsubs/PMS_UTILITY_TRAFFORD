using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using AmaanParkingSystem.Services;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VSDiagnostics;

namespace AmaanParkingSystem.Benchmarks
{
    [SimpleJob(warmupCount: 3, targetCount: 5)]
    [CPUUsageDiagnoser]
    public class CollectionServiceBenchmark
    {
        private CollectionService _collectionService;
        private DateTime _startDate;
        private DateTime _endDate;
        [GlobalSetup]
        public void Setup()
        {
            // Build configuration
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
            _collectionService = new CollectionService(config);
            // Set date range: last 7 days
            _endDate = DateTime.Today;
            _startDate = _endDate.AddDays(-7);
        }

        [Benchmark]
        public async Task GetDailyCollectionData()
        {
            var result = await _collectionService.GetDailyCollectionData(_startDate, _endDate);
        }

        [Benchmark]
        public async Task GetPaymentMethodSummary()
        {
            var result = await _collectionService.GetPaymentMethodSummary(_startDate, _endDate);
        }

        [Benchmark]
        public async Task GetDailySummaryStatistics()
        {
            var result = await _collectionService.GetDailySummaryStatistics(_startDate, _endDate);
        }

        [Benchmark]
        public async Task GetCardTypeSummary()
        {
            var result = await _collectionService.GetCardTypeSummary(_startDate, _endDate);
        }

        [Benchmark]
        public async Task GetMonthlyCollectionData()
        {
            var result = await _collectionService.GetMonthlyCollectionData(DateTime.Today.Year, DateTime.Today.Month);
        }
    }
}