﻿using System;
using System.Collections.Generic;
using System.Linq;
using NWaves.FeatureExtractors.Base;
using NWaves.Features;
using NWaves.Transforms;
using NWaves.Utils;

namespace NWaves.FeatureExtractors.Multi
{
    /// <summary>
    /// Extractor of spectral features.
    /// 
    /// It's a very flexible extractor that allows varying almost everything.
    /// It allows computing harmonic features along with spectral features.
    /// 
    /// At least one spectral feature MUST be specified.
    /// 
    /// </summary>
    public class SpectralFeaturesExtractor : FeatureExtractor
    {
        /// <summary>
        /// Names of supported spectral features
        /// </summary>
        public const string FeatureSet = "centroid, spread, flatness, noiseness, rolloff, crest, entropy, decrease, c1+c2+c3+c4+c5+c6";

        /// <summary>
        /// Names of supported harmonic features
        /// </summary>
        public const string HarmonicSet = "centroid, spread, inharmonicity, oer, t1+t2+t3";

        /// <summary>
        /// String annotations (or simply names) of features
        /// </summary>
        public override List<string> FeatureDescriptions { get; }

        /// <summary>
        /// Number of features to extract
        /// </summary>
        public override int FeatureCount => FeatureDescriptions.Count;

        /// <summary>
        /// Size of used FFT
        /// </summary>
        private readonly int _fftSize;

        /// <summary>
        /// FFT transformer
        /// </summary>
        private readonly Fft _fft;

        /// <summary>
        /// Center frequencies (uniform in Herz scale by default; could be uniform in mel-scale or octave-scale, for example)
        /// </summary>
        private readonly float[] _frequencies;

        /// <summary>
        /// Parameters
        /// </summary>
        private readonly IReadOnlyDictionary<string, object> _parameters;

        /// <summary>
        /// Internal buffer for magnitude spectrum
        /// </summary>
        private float[] _spectrum;

        /// <summary>
        /// Internal buffer for magnitude spectrum taken only at frequencies of interest
        /// </summary>
        private float[] _mappedSpectrum;

        /// <summary>
        /// Internal buffer for spectral positions of frequencies of interest
        /// </summary>
        private int[] _frequencyPositions;

        /// <summary>
        /// Internal buffer for harmonic peak frequencies (optional)
        /// </summary>
        private float[] _peakFrequencies;

        /// <summary>
        /// Internal buffer for spectral positions of harmonic peaks (optional)
        /// </summary>
        private int[] _peaks;

        /// <summary>
        /// Internal buffer for currently processed block
        /// </summary>
        private float[] _block;

        /// <summary>
        /// Internal block of zeros for a quick memset
        /// </summary>
        private float[] _zeroblock;

        /// <summary>
        /// Extractor functions
        /// </summary>
        private List<Func<float[], float[], float>> _extractors;

        /// <summary>
        /// Harmonic extractor functions (optional)
        /// </summary>
        private List<Func<float[], int[], float[], float>> _harmonicExtractors;

        /// <summary>
        /// Pitch estimator function (optional)
        /// </summary>
        private Func<float[], float> _pitchEstimator;

        /// <summary>
        /// Array of precomputed pitches (optional)
        /// </summary>
        private float[] _pitchTrack;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="samplingRate"></param>
        /// <param name="featureList"></param>
        /// <param name="frameDuration"></param>
        /// <param name="hopDuration"></param>
        /// <param name="fftSize"></param>
        /// <param name="parameters"></param>
        public SpectralFeaturesExtractor(int samplingRate,
                                         string featureList,
                                         double frameDuration = 0.0256/*sec*/,
                                         double hopDuration = 0.010/*sec*/,
                                         int fftSize = 0,
                                         float[] frequencies = null,
                                         IReadOnlyDictionary<string, object> parameters = null)

            : base(samplingRate, frameDuration, hopDuration)
        {
            if (featureList == "all" || featureList == "full")
            {
                featureList = FeatureSet;
            }

            var features = featureList.Split(',', '+', '-', ';', ':');

            _extractors = features.Select<string, Func<float[], float[], float>>(f =>
            {
                var feature = f.Trim().ToLower();
                switch (feature)
                {
                    case "sc":
                    case "centroid":
                        return Spectral.Centroid;

                    case "ss":
                    case "spread":
                        return Spectral.Spread;

                    case "sfm":
                    case "flatness":
                        if (parameters?.ContainsKey("minLevel") ?? false)
                        {
                            var minLevel = (float) parameters["minLevel"];
                            return (spectrum, freqs) => Spectral.Flatness(spectrum, minLevel);
                        }
                        else
                        {
                            return (spectrum, freqs) => Spectral.Flatness(spectrum);
                        }

                    case "sn":
                    case "noiseness":
                        if (parameters?.ContainsKey("noiseFrequency") ?? false)
                        {
                            var noiseFrequency = (float)parameters["noiseFrequency"];
                            return (spectrum, freqs) => Spectral.Noiseness(spectrum, freqs, noiseFrequency);
                        }
                        else
                        {
                            return (spectrum, freqs) => Spectral.Noiseness(spectrum, freqs);
                        }

                    case "rolloff":
                        if (parameters?.ContainsKey("rolloffPercent") ?? false)
                        {
                            var rolloffPercent = (float) parameters["rolloffPercent"];
                            return (spectrum, freqs) => Spectral.Rolloff(spectrum, freqs, rolloffPercent);
                        }
                        else
                        {
                            return (spectrum, freqs) => Spectral.Rolloff(spectrum, freqs);
                        }

                    case "crest":
                        return (spectrum, freqs) => Spectral.Crest(spectrum);

                    case "entropy":
                    case "ent":
                        return (spectrum, freqs) => Spectral.Entropy(spectrum);

                    case "sd":
                    case "decrease":
                        return (spectrum, freqs) => Spectral.Decrease(spectrum);

                    case "c1":
                    case "c2":
                    case "c3":
                    case "c4":
                    case "c5":
                    case "c6":
                        return (spectrum, freqs) => Spectral.Contrast(spectrum, freqs, int.Parse(feature.Substring(1)));

                    default:
                        return (spectrum, freqs) => 0;
                }
            }).ToList();

            FeatureDescriptions = features.ToList();
            
            _fftSize = fftSize > FrameSize ? fftSize : MathUtils.NextPowerOfTwo(FrameSize);
            _fft = new Fft(_fftSize);

            var resolution = (float)samplingRate / _fftSize;

            if (frequencies == null)
            {
                _frequencies = Enumerable.Range(0, _fftSize / 2 + 1)
                                         .Select(f => f * resolution)
                                         .ToArray();
            }
            else
            {
                _frequencies = frequencies;
                _mappedSpectrum = new float[_frequencies.Length];
                _frequencyPositions = new int[_frequencies.Length];

                for (var i = 0; i < _frequencies.Length; i++)
                {
                    _frequencyPositions[i] = (int)(_frequencies[i] / resolution) + 1;
                }
            }

            _parameters = parameters;

            // reserve memory for reusable blocks

            _spectrum = new float[_fftSize / 2 + 1];  // buffer for magnitude spectrum
            _block = new float[_fftSize];             // buffer for currently processed block
            _zeroblock = new float[_fftSize];         // just a buffer of zeros for quick memset
        }

        /// <summary>
        /// Add one more feature with routine for its calculation
        /// </summary>
        /// <param name="name"></param>
        /// <param name="algorithm"></param>
        public void AddFeature(string name, Func<float[], float[], float> algorithm)
        {
            FeatureDescriptions.Insert(_extractors.Count, name);
            _extractors.Add(algorithm);
        }

        /// <summary>
        /// Add set of harmonic features to calculation list
        /// </summary>
        /// <param name="featureList"></param>
        /// <param name="peakCount"></param>
        /// <param name="pitchEstimator"></param>
        /// <param name="lowPitch"></param>
        /// <param name="highPitch"></param>
        public void IncludeHarmonicFeatures(string featureList,
                                            int peakCount = 10,
                                            Func<float[], float> pitchEstimator = null,
                                            float lowPitch = 80,
                                            float highPitch = 400)
        {
            if (featureList == "all" || featureList == "full")
            {
                featureList = HarmonicSet;
            }

            var features = featureList.Split(',', '+', '-', ';', ':');

            _harmonicExtractors = features.Select<string, Func<float[], int[], float[], float>>(f =>
            {
                var feature = f.Trim().ToLower();
                switch (feature)
                {
                    case "hc":
                    case "centroid":
                        return Harmonic.Centroid;

                    case "hs":
                    case "spread":
                        return Harmonic.Spread;

                    case "inh":
                    case "inharmonicity":
                        return Harmonic.Inharmonicity;

                    case "oer":
                    case "oddevenratio":
                        return (spectrum, peaks, freqs) => Harmonic.OddToEvenRatio(spectrum, peaks);

                    case "t1":
                    case "t2":
                    case "t3":
                        return (spectrum, peaks, freqs) => Harmonic.Tristimulus(spectrum, peaks, int.Parse(feature.Substring(1)));

                    default:
                        return (spectrum, peaks, freqs) => 0;
                }
            }).ToList();

            FeatureDescriptions.AddRange(features);

            if (pitchEstimator == null)
            {
                _pitchEstimator = spectrum => Pitch.FromSpectralPeaks(spectrum, SamplingRate, lowPitch, highPitch);
            }
            else
            {
                _pitchEstimator = pitchEstimator;
            }

            _peaks = new int[peakCount];
            _peakFrequencies = new float[peakCount];
        }

        /// <summary>
        /// Add one more harmonic feature with routine for its calculation
        /// </summary>
        /// <param name="name"></param>
        /// <param name="algorithm"></param>
        public void AddHarmonicFeature(string name, Func<float[], int[], float[], float> algorithm)
        {
            if (_harmonicExtractors == null)
            {
                return;
            }

            FeatureDescriptions.Add(name);
            _harmonicExtractors.Add(algorithm);
        }

        /// <summary>
        /// Set array of precomputed pitches
        /// </summary>
        /// <param name="pitchTrack"></param>
        public void SetPitchTrack(float[] pitchTrack)
        {
            _pitchTrack = pitchTrack;
        }

        /// <summary>
        /// Compute the sequence of feature vectors from some fragment of a signal
        /// </summary>
        /// <param name="samples">Signal</param>
        /// <param name="startSample">The number (position) of the first sample for processing</param>
        /// <param name="endSample">The number (position) of last sample for processing</param>
        /// <returns>Sequence of feature vectors</returns>
        public override List<FeatureVector> ComputeFrom(float[] samples, int startSample, int endSample)
        {
            Guard.AgainstInvalidRange(startSample, endSample, "starting pos", "ending pos");

            var nullExtractorPos = _extractors.IndexOf(null);
            if (nullExtractorPos >= 0)
            {
                throw new ArgumentException($"Unknown feature: {FeatureDescriptions[nullExtractorPos]}");
            }
            
            var featureVectors = new List<FeatureVector>();
            var featureCount = FeatureCount;

            var pitchPos = startSample / HopSize;

            var i = startSample;
            while (i + FrameSize < endSample)
            {
                // prepare all blocks in memory for the current step:

                _zeroblock.FastCopyTo(_block, _fftSize);
                samples.FastCopyTo(_block, FrameSize, i);

                // compute and prepare spectrum

                _fft.MagnitudeSpectrum(_block, _spectrum);

                var featureVector = new float[featureCount];

                if (_extractors != null)
                {
                    if (_spectrum.Length == _frequencies.Length)
                    {
                        _mappedSpectrum = _spectrum;
                    }
                    else
                    {
                        for (var j = 0; j < _mappedSpectrum.Length; j++)
                        {
                            _mappedSpectrum[j] = _spectrum[_frequencyPositions[j]];
                        }
                    }

                    // extract spectral features

                    for (var j = 0; j < _extractors.Count; j++)
                    {
                        featureVector[j] = _extractors[j](_mappedSpectrum, _frequencies);
                    }
                }

                // ...and maybe harmonic features

                if (_harmonicExtractors != null)
                {
                    var pitch = _pitchTrack == null ? _pitchEstimator(_spectrum) : _pitchTrack[pitchPos++];

                    Harmonic.Peaks(_spectrum, _peaks, _peakFrequencies, SamplingRate, pitch);

                    var offset = _extractors.Count;
                    for (var j = 0; j < _harmonicExtractors.Count; j++)
                    {
                        featureVector[j + offset] = _harmonicExtractors[j](_spectrum, _peaks, _peakFrequencies);
                    }
                }

                // finally create new feature vector

                featureVectors.Add(new FeatureVector
                {
                    Features = featureVector,
                    TimePosition = (double)i / SamplingRate
                });

                i += HopSize;
            }

            return featureVectors;
        }

        /// <summary>
        /// True if computations can be done in parallel
        /// </summary>
        /// <returns></returns>
        public override bool IsParallelizable() => true;

        /// <summary>
        /// Copy of current extractor that can work in parallel
        /// </summary>
        /// <returns></returns>
        public override FeatureExtractor ParallelCopy()
        {
            var spectralFeatureSet = string.Join(",", FeatureDescriptions.Take(_extractors.Count));
            
            var copy = new SpectralFeaturesExtractor(SamplingRate, spectralFeatureSet, FrameDuration, HopDuration, _fftSize, _frequencies, _parameters)
            {
                _extractors = _extractors,
                _pitchTrack = _pitchTrack
            };

            if (_harmonicExtractors != null)
            {
                var harmonicFeatureSet = string.Join(",", FeatureDescriptions.Skip(_extractors.Count));
                copy.IncludeHarmonicFeatures(harmonicFeatureSet, _peaks.Length, _pitchEstimator);
            }

            return copy;
        }
    }
}
