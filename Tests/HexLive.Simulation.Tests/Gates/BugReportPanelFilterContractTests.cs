using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
    public sealed class BugReportPanelFilterContractTests
    {
        private string _source;

        [SetUp]
        public void ReadPanelSource()
        {
            _source = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "UI", "BugReportPanel.cs"));
        }

        [Test]
        public void MarkFixedRefreshesThroughFilterPreservingPolicy()
        {
            var callback = Slice("BugReportStore.MarkFixed(report.id);", "bugs.rework");

            Assert.That(callback, Does.Contain("RefreshManagerAfterStatusChange(ManagerTab.Fixed);"));
            Assert.That(callback, Does.Not.Contain("_tab = ManagerTab.Fixed;"));
        }

        [Test]
        public void StatusChangeNavigatesOnlyWhenNoStatusFilterIsActive()
        {
            var policy = Slice(
                "private void RefreshManagerAfterStatusChange",
                "private static string StatusLabel");

            Assert.Multiple(() =>
            {
                Assert.That(policy, Does.Contain("if (_statusFilter == null)"));
                Assert.That(policy, Does.Contain("_tab = destinationWithoutFilter;"));
                Assert.That(policy, Does.Contain("RebuildManager();"));
            });
        }

        private string Slice(string startMarker, string endMarker)
        {
            var start = _source.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing marker: {startMarker}");
            var end = _source.IndexOf(endMarker, start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start), $"Missing marker after start: {endMarker}");
            return _source[start..end];
        }
    }
}
