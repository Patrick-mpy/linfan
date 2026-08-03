// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using LinFan.App.Controllers;
using LinFan.App.Converters;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert die Logik des <see cref="EnumMatchConverter"/> ab — genau der Konverter, der die Schritt-
/// Sichtbarkeit des Onboarding-Assistenten steuert. Reine C#-Logik, ohne Avalonia-Headless testbar.
/// </summary>
public sealed class EnumMatchConverterTests
{
    private static object? Convert(object? value, string? parameter) =>
        EnumMatchConverter.Instance.Convert(value, typeof(bool), parameter, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_SingleToken_MatchesExactStep() =>
        Assert.Equal(true, Convert(OnboardingStep.Calibration, "Calibration"));

    [Fact]
    public void Convert_SingleToken_NonMatchingStep_IsFalse() =>
        Assert.Equal(false, Convert(OnboardingStep.Welcome, "Calibration"));

    [Theory]
    [InlineData(OnboardingStep.Calibration, true)]
    [InlineData(OnboardingStep.ChooseProfile, true)]
    [InlineData(OnboardingStep.Done, true)]
    [InlineData(OnboardingStep.Welcome, false)]
    public void Convert_MultiTokenSet_MatchesAnyListedStep(OnboardingStep step, bool expected) =>
        Assert.Equal(expected, Convert(step, "Calibration,ChooseProfile,Done"));

    [Fact]
    public void Convert_TokensWithWhitespace_AreTrimmed() =>
        Assert.Equal(true, Convert(OnboardingStep.ChooseProfile, " Welcome , ChooseProfile "));

    [Fact]
    public void Convert_NullValue_IsFalse() =>
        Assert.Equal(false, Convert(null, "Welcome"));

    [Fact]
    public void Convert_NonStringParameter_IsFalse() =>
        Assert.Equal(false, Convert(OnboardingStep.Welcome, null));
}
