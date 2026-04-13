using System.Xml.Linq;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection("Pipeline")]
public class JUnitOutputTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(RepoRoot, "tests", testCase, sub);

    // --- WriteJUnit unit tests ---

    [Fact]
    public void WriteJUnit_PassingTests_ProducesValidXml()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Pass, DurationMs = 42, CodeunitName = "MySuite" },
            new() { Name = "TestB", Status = TestStatus.Pass, DurationMs = 10, CodeunitName = "MySuite" }
        };

        var path = Path.GetTempFileName();
        try
        {
            JUnitReport.WriteJUnit(path, results);
            var doc = XDocument.Load(path);

            Assert.Equal("testsuites", doc.Root!.Name.LocalName);
            Assert.Single(doc.Root.Elements("testsuite"));

            var suite = doc.Root.Element("testsuite")!;
            Assert.Equal("MySuite", suite.Attribute("name")!.Value);
            Assert.Equal("2", suite.Attribute("tests")!.Value);
            Assert.Equal("0", suite.Attribute("failures")!.Value);
            Assert.Equal("0", suite.Attribute("errors")!.Value);

            var testcases = suite.Elements("testcase").ToList();
            Assert.Equal(2, testcases.Count);
            Assert.Equal("TestA", testcases[0].Attribute("name")!.Value);
            Assert.Equal("MySuite", testcases[0].Attribute("classname")!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteJUnit_FailingTest_UsesFailureElement()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestFail", Status = TestStatus.Fail, DurationMs = 5, Message = "Expected 1 but got 2", CodeunitName = "MySuite" }
        };

        var path = Path.GetTempFileName();
        try
        {
            JUnitReport.WriteJUnit(path, results);
            var doc = XDocument.Load(path);

            var tc = doc.Descendants("testcase").Single();
            var failure = tc.Element("failure");
            Assert.NotNull(failure);
            Assert.Equal("Expected 1 but got 2", failure!.Attribute("message")!.Value);
            Assert.Null(tc.Element("error")); // must not be an <error>
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteJUnit_ErrorTest_UsesErrorElement()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestError", Status = TestStatus.Error, DurationMs = 0, Message = "NotSupportedException: page not supported", CodeunitName = "MySuite" }
        };

        var path = Path.GetTempFileName();
        try
        {
            JUnitReport.WriteJUnit(path, results);
            var doc = XDocument.Load(path);

            var tc = doc.Descendants("testcase").Single();
            var error = tc.Element("error");
            Assert.NotNull(error);
            Assert.Contains("NotSupportedException", error!.Attribute("message")!.Value);
            Assert.Null(tc.Element("failure")); // must not be a <failure>
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteJUnit_MultipleCodeunits_ProducesMultipleSuites()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Pass, DurationMs = 1, CodeunitName = "SuiteOne" },
            new() { Name = "TestB", Status = TestStatus.Pass, DurationMs = 2, CodeunitName = "SuiteTwo" },
            new() { Name = "TestC", Status = TestStatus.Fail, DurationMs = 3, Message = "fail", CodeunitName = "SuiteOne" }
        };

        var path = Path.GetTempFileName();
        try
        {
            JUnitReport.WriteJUnit(path, results);
            var doc = XDocument.Load(path);

            var suites = doc.Root!.Elements("testsuite").ToList();
            Assert.Equal(2, suites.Count);

            var suiteOne = suites.First(s => s.Attribute("name")!.Value == "SuiteOne");
            var suiteTwo = suites.First(s => s.Attribute("name")!.Value == "SuiteTwo");

            Assert.Equal("2", suiteOne.Attribute("tests")!.Value);
            Assert.Equal("1", suiteOne.Attribute("failures")!.Value);
            Assert.Equal("1", suiteTwo.Attribute("tests")!.Value);
            Assert.Equal("0", suiteTwo.Attribute("failures")!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteJUnit_Timing_IsInSeconds()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestA", Status = TestStatus.Pass, DurationMs = 1500, CodeunitName = "MySuite" }
        };

        var path = Path.GetTempFileName();
        try
        {
            JUnitReport.WriteJUnit(path, results);
            var doc = XDocument.Load(path);

            var tc = doc.Descendants("testcase").Single();
            var timeStr = tc.Attribute("time")!.Value;
            var time = double.Parse(timeStr, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(1.5, time, precision: 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteJUnit_Counts_AreCorrect()
    {
        var results = new List<TestResult>
        {
            new() { Name = "TestPass", Status = TestStatus.Pass, DurationMs = 1, CodeunitName = "Suite" },
            new() { Name = "TestFail", Status = TestStatus.Fail, DurationMs = 2, Message = "oops", CodeunitName = "Suite" },
            new() { Name = "TestError", Status = TestStatus.Error, DurationMs = 0, Message = "nope", CodeunitName = "Suite" }
        };

        var path = Path.GetTempFileName();
        try
        {
            JUnitReport.WriteJUnit(path, results);
            var doc = XDocument.Load(path);

            var root = doc.Root!;
            Assert.Equal("3", root.Attribute("tests")!.Value);
            Assert.Equal("1", root.Attribute("failures")!.Value);
            Assert.Equal("1", root.Attribute("errors")!.Value);

            var suite = root.Element("testsuite")!;
            Assert.Equal("3", suite.Attribute("tests")!.Value);
            Assert.Equal("1", suite.Attribute("failures")!.Value);
            Assert.Equal("1", suite.Attribute("errors")!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteJUnit_EmptyResults_ProducesEmptyTestsuites()
    {
        var path = Path.GetTempFileName();
        try
        {
            JUnitReport.WriteJUnit(path, new List<TestResult>());
            var doc = XDocument.Load(path);

            Assert.Equal("testsuites", doc.Root!.Name.LocalName);
            Assert.Equal("0", doc.Root.Attribute("tests")!.Value);
            Assert.Empty(doc.Root.Elements("testsuite"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Integration test via AlRunnerPipeline ---

    [Fact]
    public void OutputJunit_Integration_CreatesFileWithTestResults()
    {
        var junitPath = Path.GetTempFileName();
        try
        {
            var pipeline = new AlRunnerPipeline();
            var result = pipeline.Run(new PipelineOptions
            {
                InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") },
                OutputJunitPath = junitPath
            });

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(junitPath), "JUnit file should have been written");
            Assert.True(new FileInfo(junitPath).Length > 0, "JUnit file should not be empty");

            var doc = XDocument.Load(junitPath);
            Assert.Equal("testsuites", doc.Root!.Name.LocalName);

            var testcases = doc.Descendants("testcase").ToList();
            Assert.NotEmpty(testcases);
            Assert.All(testcases, tc =>
            {
                Assert.Null(tc.Element("failure"));
                Assert.Null(tc.Element("error"));
            });
        }
        finally
        {
            if (File.Exists(junitPath)) File.Delete(junitPath);
        }
    }

    [Fact]
    public void OutputJunit_NoTestsRun_DoesNotCreateFile()
    {
        // When no tests are found (e.g. empty source), the pipeline exits early
        // and OutputJunitPath should not produce a file (or leaves an empty one).
        // Current behaviour: file is only written when testResults.Count > 0.
        var junitPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            var pipeline = new AlRunnerPipeline();
            pipeline.Run(new PipelineOptions
            {
                OutputJunitPath = junitPath
                // no InputPaths — triggers "no AL source" early exit
            });

            // File should not have been written (no tests ran)
            Assert.False(File.Exists(junitPath), "JUnit file should not be created when no tests ran");
        }
        finally
        {
            if (File.Exists(junitPath)) File.Delete(junitPath);
        }
    }
}
