﻿using System.IO;
using RawLibrary;
using System;
using System.Linq;
using System.Collections.Generic;
using Emgu.CV;
using Emgu.CV.ML;
using System.Drawing;
using System.Runtime.InteropServices;
using Emgu.CV.Util;
using System.Diagnostics;
using System.Xml.Serialization;
using Emgu.Util;
using System.Xml.Linq;
using AnomalyModel;
using System.Text;

namespace AnomalyDetection
{
    public class Program
    {
        // Save / load segmentation and feature extraction results to speed up development on classifier
        // and hyperparameter search.
        private const bool DoSave = false;
        private const bool DoLoad = false;

        private const string PathToVideo = @"D:\Users\Michel\Documents\FH\module\8_woipv\input\videos\Seil_2_2016-05-23_RAW3\2016-05-23_15-02-14.raw3";
        private const string PathToAnnotation = @"D:\Users\Michel\Documents\FH\module\8_woipv\input\videos\Seil_2_2016-05-23_RAW3\2016-05-23_15-02-14.v2.anomaly_based.ann";

        private const string NSamplesPerFrameFile = "nSamplesPerFrame.txt";
        private const string CellWidthFile = "cellWidth.txt";
        private const string XTrainFile = "XTrain.xml";
        private const string XTestNormalFile = "XTestNormal.xml";
        private const string XTestAnomalyFile = "XTestAnomaly.xml";

        public static void Main(string[] args)
        {
            // Load anomaly annotations for video
            var annotations = AnnotationsReader.Read(PathToAnnotation);

            // Shuffle normal frames before train-test-split to make test set more diverse. Use seed for reproducibility.
            // Otherwise, shuffling seems to have no effect on SVM training so this is the only place we have to do it:
            // http://stackoverflow.com/questions/20731214/is-it-required-to-shuffle-the-training-data-for-svm-multi-classification
            var random = new Random(42);

            // Take as many normal frames as we have anomaly frames for testing.
            // The reason for doing this is that the number of labeled frames is very limited and we want to use as many as possible for training.
            //
            // This is certainly not the perfect solution since it obviously changes the distribution of normal / anomaly samples pretty drastically.
            // Since this is only relevant for false positives and the false positive metric is relative to the number of frames (percentage of frames with false positives),
            // this is tolerable. Still this metric is slightly skewed since normal frames contain more false positives than anomaly frames.
            var normalFramesTest = annotations.NormalFrames
                .OrderBy(l => random.NextDouble())
                .Take(annotations.AnomalyFrames.Length)
                .ToArray();
            var normalFramesTrain = annotations.NormalFrames.Except(normalFramesTest).ToArray();

            int nSamplesPerFrame;
            int cellWidth;
            Mat XTrain;
            Mat XTestNormal;
            Mat XTestAnomaly;
            if (DoLoad)
            {
                Console.WriteLine($"[{DateTime.Now}] Program.Main: reading matrices from filesystem");

                nSamplesPerFrame = int.Parse(File.ReadAllText(NSamplesPerFrameFile));
                cellWidth = int.Parse(File.ReadAllText(CellWidthFile));
                XTrain = new Mat();
                new FileStorage(XTrainFile, FileStorage.Mode.Read).GetFirstTopLevelNode().ReadMat(XTrain);
                XTestNormal = new Mat();
                new FileStorage(XTestNormalFile, FileStorage.Mode.Read).GetFirstTopLevelNode().ReadMat(XTestNormal);
                XTestAnomaly = new Mat();
                new FileStorage(XTestAnomalyFile, FileStorage.Mode.Read).GetFirstTopLevelNode().ReadMat(XTestAnomaly);
            }
            else
            {
                // Fit feature extractor and extract features from video for the normal, anomaly-free frames of the training set.
                var raw = new RawImage(new FileInfo(PathToVideo));
                var featureTransformer = new HogTransformer();
                XTrain = featureTransformer.FitTransform(raw, normalFramesTrain);
                Debug.Assert(XTrain.Rows % normalFramesTrain.Length == 0);
                nSamplesPerFrame = XTrain.Rows / normalFramesTrain.Length;
                cellWidth = featureTransformer.CellWidth;

                // Extract features from video for test frames.
                XTestNormal = featureTransformer.Transform(raw, normalFramesTest);
                XTestAnomaly = featureTransformer.Transform(raw, annotations.AnomalyFrames);

                // Standardize data to mean 0, standard deviation 1.
                var stdScaler = new StandardScaler();
                stdScaler.Fit(XTrain);
                XTrain = stdScaler.Transform(XTrain);
                Console.WriteLine($"{XTrain.Rows} x {XTrain.Cols}");
                XTestNormal = stdScaler.Transform(XTestNormal);
                XTestAnomaly = stdScaler.Transform(XTestAnomaly);

                if (DoSave)
                {
                    Console.WriteLine($"[{DateTime.Now}] Program.Main: writing matrices to filesystem");

                    File.WriteAllText(NSamplesPerFrameFile, nSamplesPerFrame.ToString());
                    File.WriteAllText(CellWidthFile, cellWidth.ToString());
                    new FileStorage(XTrainFile, FileStorage.Mode.Write).Write(XTrain);
                    new FileStorage(XTestNormalFile, FileStorage.Mode.Write).Write(XTestNormal);
                    new FileStorage(XTestAnomalyFile, FileStorage.Mode.Write).Write(XTestAnomaly);
                }
            }

            // Create label vector for anomaly frames test set.
            // Start off by labeling every sample as normal.
            var yTestAnomaly = new int[XTestAnomaly.Rows];
            for (int i = 0; i < yTestAnomaly.Length; ++i)
            {
                yTestAnomaly[i] = 1;
            }

            // Create a map of anomaly ids, which mark different anomaly regions as part of the same actual anomaly
            // which can usually be seen on multiple frames, to all corresponding indices in the yTestAnomaly label vector
            // to be able to calculate per-anomaly metrics.
            var anomalyIdToYTestAnomalyIndices = new Dictionary<string, ISet<int>>();
            
            // Relabel samples which are part of an annotated anomaly region to anomaly samples.
            foreach (var anomalyRegion in annotations.AnomalyRegions)
            {
                int offset = Array.IndexOf(annotations.AnomalyFrames, anomalyRegion.Frame) * nSamplesPerFrame;
                // Math.Min is necessary because the last pixel columns that don't make up a whole cell are truncated by the feature transformer.
                for (int i = anomalyRegion.XStart / cellWidth; i < Math.Min(nSamplesPerFrame, anomalyRegion.XEnd / cellWidth + 1); ++i)
                {
                    yTestAnomaly[offset + i] = 0;
                    if (!anomalyIdToYTestAnomalyIndices.ContainsKey(anomalyRegion.AnomalyId))
                    {
                        anomalyIdToYTestAnomalyIndices[anomalyRegion.AnomalyId] = new HashSet<int>();
                    }
                    anomalyIdToYTestAnomalyIndices[anomalyRegion.AnomalyId].Add(offset + i);
                }
            }

            // Mark unclear regions in yTestAnomaly so FPs in these regions can be filtered out.
            var yTestAnomalyUnclearIndices = new HashSet<int>();
            foreach (var unclearRegion in annotations.UnclearRegions)
            {
                int index = Array.IndexOf(annotations.AnomalyFrames, unclearRegion.Frame);
                if (index != -1)
                {
                    int offset = index * nSamplesPerFrame;
                    // Math.Min is necessary because the last pixel columns that don't make up a whole cell are truncated by the feature transformer.
                    for (int i = unclearRegion.XStart / cellWidth; i < Math.Min(nSamplesPerFrame, unclearRegion.XEnd / cellWidth + 1); ++i)
                    {
                        yTestAnomalyUnclearIndices.Add(offset + i);
                    }
                }
            }

            Console.WriteLine($"XTrain shape = {XTrain.Rows} x {XTrain.Cols}");
            FitPredict(
                XTrain, XTestNormal, XTestAnomaly, yTestAnomaly,
                anomalyIdToYTestAnomalyIndices, yTestAnomalyUnclearIndices,
                nSamplesPerFrame
            );
            //GridSearch(
            //    XTrain, XTestNormal, XTestAnomaly, yTestAnomaly,
            //    anomalyIdToYTestAnomalyIndices, yTestAnomalyUnclearIndices,
            //    nSamplesPerFrame
            //);
        }

        /// <summary>
        /// Fit one-class SVM to XTrain feature matrix, predict train and test sets and print metrics.
        /// </summary>
        /// <param name="XTrain"></param>
        /// <param name="XTestNormal"></param>
        /// <param name="XTestAnomaly"></param>
        /// <param name="yTestAnomaly"></param>
        /// <param name="anomalyIdToYTestAnomalyIndices"></param>
        /// <param name="yTestAnomalyUnclearIndices"></param>
        /// <param name="nSamplesPerFrame"></param>
        private static void FitPredict(
            Mat XTrain, Mat XTestNormal, Mat XTestAnomaly, int[] yTestAnomaly,
            Dictionary<string, ISet<int>> anomalyIdToYTestAnomalyIndices, HashSet<int> yTestAnomalyUnclearIndices,
            int nSamplesPerFrame
        )
        {
            var classifier = new SvmOneClassClassifier();

            // Best hyperparameters as found by the GridSearch method.
            classifier.Fit(XTrain, 0.01, 0.01);

            Console.WriteLine();
            Console.WriteLine("TRAIN");
            var yTrainPredicted = classifier.Predict(XTrain);
            MetricsUtil.PrintMetrics(yTrainPredicted, nSamplesPerFrame);
            Console.WriteLine();

            Console.WriteLine("TEST");
            var yTestNormalPredicted = classifier.Predict(XTestNormal);
            var yTestAnomalyPredicted = classifier.Predict(XTestAnomaly);
            MetricsUtil.PrintMetrics(
                yTestNormalPredicted, nSamplesPerFrame,
                yTestAnomaly, yTestAnomalyPredicted, anomalyIdToYTestAnomalyIndices, yTestAnomalyUnclearIndices
            );
        }

        /// <summary>
        /// Fit an SVM for all combinations of a hyperparameter grid and calculate metrics
        /// to optimize hyperparameters.
        /// Prints results in order, best parameter combinations first.
        /// Also writes a CSV file containing the results of all combinations.
        /// See body for details on what's considered "best".
        /// </summary>
        /// <param name="XTrain"></param>
        /// <param name="XTestNormal"></param>
        /// <param name="XTestAnomaly"></param>
        /// <param name="yTestAnomaly"></param>
        /// <param name="anomalyIdToYTestAnomalyIndices"></param>
        /// <param name="yTestAnomalyUnclearIndices"></param>
        /// <param name="nSamplesPerFrame"></param>
        /// <param name="resultGridCsvFile"></param>
        /// <param name="csvSeparator"></param>
        private static void GridSearch(
            Mat XTrain, Mat XTestNormal, Mat XTestAnomaly, int[] yTestAnomaly,
            Dictionary<string, ISet<int>> anomalyIdToYTestAnomalyIndices, HashSet<int> yTestAnomalyUnclearIndices,
            int nSamplesPerFrame, string resultGridCsvFile = "grid_search_results.csv", string csvSeparator = ";"
        )
        {
            // Logarithmic grids with base 10 for gamma and nu hyperparameters.
            var gammaValues = new double[] {
                0.0001, 0.001, 0.01, 0.1, 1.0, 10.0, 100.0, 1000.0,
            };
            var nuValues = new double[] {
                0.00001, 0.0001, 0.001, 0.01, 0.1,
            };

            var results = new List<Tuple<double, double, Metrics>>();
            var resultGridCsv = new StringBuilder();
            resultGridCsv.AppendLine(csvSeparator + string.Join(csvSeparator, gammaValues.Select(d => d.ToString())));
            foreach (double nu in nuValues)
            {
                resultGridCsv.Append(nu);
                foreach (double gamma in gammaValues)
                {
                    Console.WriteLine($"gamma={gamma}, nu={nu}");
                    var classifier = new SvmOneClassClassifier();
                    classifier.Fit(XTrain, gamma, nu);
                    var yTestNormalPredicted = classifier.Predict(XTestNormal);
                    var yTestAnomalyPredicted = classifier.Predict(XTestAnomaly);
                    var metrics = MetricsUtil.CalculateMetrics(
                        yTestNormalPredicted, nSamplesPerFrame,
                        yTestAnomaly, yTestAnomalyPredicted, anomalyIdToYTestAnomalyIndices, yTestAnomalyUnclearIndices
                    );

                    results.Add(Tuple.Create(gamma, nu, metrics));
                    resultGridCsv.Append(csvSeparator + $"{metrics.RecallAnomalyLevel} / {metrics.Recall:F4} / {metrics.FpFramesPercentage:F2}");
                }
                resultGridCsv.AppendLine();
            }

            // Filter results: a maximum of 50% of test frames containing FPs are tolerated.
            // Sort results: The goal is to maximize recall and find all anomalies. If the recall on anomaly level is equal, sort by recall on sample level.
            foreach (var result in results.Where(r => r.Item3.FpFramesPercentage < 50.0).OrderByDescending(r => r.Item3.TpAnomalyLevel).ThenByDescending(r => r.Item3.Recall))
            {
                Console.WriteLine($"gamma={result.Item1}, nu={result.Item2}");
                MetricsUtil.PrintMetrics(result.Item3);
            }

            File.WriteAllText(resultGridCsvFile, resultGridCsv.ToString());
        }
    }
}
