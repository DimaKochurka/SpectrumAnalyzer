using System;
using System.Linq;
using System.Numerics;
using FftSharp;

namespace SpectrumAnalyzer.Services;

public class AudioService
{
    private const int FftSize = 4096;
    public int SampleRate { get; set; } = 44100;

    public double[] ProcessFft(double[] audioData, out double spectralCentroid)
    {
        // Prepare a fixed-size frame for FFT analysis.
        // Shorter input blocks are zero-padded; longer blocks are truncated to one FFT frame.
        double[] preparedData = new double[FftSize];
        int lengthToCopy = Math.Min(audioData.Length, FftSize);
        Array.Copy(audioData, preparedData, lengthToCopy);

        // Apply a Hann window to reduce spectral leakage before the FFT.
        double[] windowed = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            double multiplier = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
            windowed[i] = preparedData[i] * multiplier;
        }

        // Transform the time-domain audio frame into the frequency domain.
        System.Numerics.Complex[] spectrum = FftSharp.FFT.Forward(windowed);

        double[] fftMagnitude = FftSharp.FFT.Magnitude(spectrum);

        int targetLength = FftSize / 2;
        int availableData = Math.Min(fftMagnitude.Length, targetLength);

        // Calculate the spectral centroid from linear FFT magnitudes.
        // It represents the spectral "center of mass" and is used as a timbre feature.
        double sumWeightedFrequencies = 0;
        double sumMagnitudes = 0;
        for (int i = 0; i < availableData; i++)
        {
            double frequency = (double)i * SampleRate / FftSize;
            sumWeightedFrequencies += frequency * fftMagnitude[i];
            sumMagnitudes += fftMagnitude[i];
        }

        spectralCentroid = sumMagnitudes == 0 ? 0 : sumWeightedFrequencies / sumMagnitudes;

        // Convert the linear magnitude spectrum to decibels for visualization
        // and further feature analysis. The offset approximately compensates
        // for the coherent gain of the Hann window.
        double[] dbSpectrum = new double[targetLength];

        for (int i = 0; i < availableData; i++)
        {
            double mag = fftMagnitude[i];
            if (mag < 0.0000001) mag = 0.0000001;

            double db = 20 * Math.Log10(mag) + 6.02;
            if (db < -100) db = -100;

            dbSpectrum[i] = db;
        }

        for (int i = availableData; i < targetLength; i++)
        {
            dbSpectrum[i] = -100;
        }

        return dbSpectrum;
    }

    public double GetDominantFrequency(double[] dbSpectrum)
    {
        double maxDb = -1000;
        int maxIndex = 0;

        // Ignore frequencies below 20 Hz when searching for the dominant frequency
        // to reduce the influence of DC offset and very-low-frequency components.
        int startIndex = (int)(20.0 * FftSize / SampleRate);
        if (startIndex < 0 || startIndex >= dbSpectrum.Length) startIndex = 0;

        for (int i = startIndex; i < dbSpectrum.Length; i++)
        {
            if (dbSpectrum[i] > maxDb)
            {
                maxDb = dbSpectrum[i];
                maxIndex = i;
            }
        }

        return (double)maxIndex * SampleRate / FftSize;
    }

    // Estimate whether the spectrum is predominantly tonal, harmonic, or noise-like
    // from the relationship between its peak and average spectral levels.
    public string GetAudioStructure(double[] dbSpectrum)
    {
        if (dbSpectrum.Length == 0) return "---";

        var activeData = dbSpectrum.Where(db => db > -95).ToArray();
        if (activeData.Length == 0) return "Noise";

        double maxDb = activeData.Max();
        double averageDb = activeData.Average();
        double difference = maxDb - averageDb;

        if (difference > 40) return "Tonal";
        if (difference > 18) return "Harmonic";

        return "Noise";
    }

    // Classify the sound source using heuristic rules derived from spectral features.
    // This is a rule-based classifier, not a machine-learning model.
    public string PredictSource(double dominantFreq, double centroid, string structure, double peakDb)
    {
        if (peakDb < -60) return "No source (Silence)";

        if (structure == "Noise")
        {
            if (centroid < 600) return "Low-Frequency Noise";
            if (centroid > 4000) return "High-Frequency Noise";
            return "Background Noise";
        }

        // Relate spectral brightness to the dominant frequency.
        // This feature helps distinguish sources with different harmonic distributions.
        double harmonicSpread = centroid / (dominantFreq + 0.1);

        if (harmonicSpread > 11.0)
        {
            return "Human Voice";
        }

        if (dominantFreq >= 80 && dominantFreq <= 450 && centroid >= 800 && centroid <= 3500 && harmonicSpread > 3.0)
        {
            bool isGuitarTrap = (harmonicSpread > 6.8) || (dominantFreq < 260 && harmonicSpread >= 4.5);

            if (!isGuitarTrap)
            {
                return "Human Voice";
            }
        }

        if (structure == "Tonal" && harmonicSpread < 1.5)
        {
            if (dominantFreq < 300) return "Pure Low-Frequency Tone";
            return "Pure Tone";
        }

        if (structure == "Tonal" && harmonicSpread < 3.5 && dominantFreq > 300)
        {
            return "Monophonic Instrument";
        }

        if (dominantFreq < 130)
        {
            if (harmonicSpread <= 11.0)
                return "Low-Frequency String Instrument";
        }

        if (dominantFreq < 1500)
        {
            if (harmonicSpread > 4.5)
                return "Bright-Sounding Instrument";

            return "Polyphonic Instrument";
        }

        return "High Harmonic Instrument";
    }


}