// Copyright © Clinton Ingram and Contributors.  Licensed under the MIT License.

using System;
using System.Numerics;

namespace PhotoSauce.MagicScaler.Experimental;

public interface IColorProfile
{
	bool IsValid { get; }

	ReadOnlySpan<byte> ProfileBytes { get; }

	ColorProfileType ProfileType { get; }

	ProfileColorSpace DataColorSpace { get; }

	ProfileColorSpace PcsColorSpace { get; }

	internal ColorProfile Get();
}

public interface ICurveProfile
	: IColorProfile
{
	bool IsLinear { get; }

	IProfileCurve Curve { get; }

	// TODO (or not): CompactProfile
}

public interface IMatrixProfile
	: ICurveProfile
{
	Matrix4x4 Matrix { get; }

	Matrix4x4 InverseMatrix { get; }
}

public interface IProfileCurve
{
	ReadOnlySpan<float> Gamma { get; }

	ReadOnlySpan<float> InverseGamma { get; }
}

public static class ColorProfileFactory
{
	public static ICurveProfile sGrey { get; } = new CurveProfileWrapper(ColorProfile.sGrey);
	public static IMatrixProfile sRGB { get; } = new MatrixProfileWrapper(ColorProfile.sRGB);
	public static IMatrixProfile AdobeRgb { get; } = new MatrixProfileWrapper(ColorProfile.AdobeRgb);
	public static IMatrixProfile DisplayP3 { get; } = new MatrixProfileWrapper(ColorProfile.DisplayP3);
	public static IColorProfile CmykDefault { get; } = new ColorProfileWrapper(ColorProfile.CmykDefault);

	public static IColorProfile Parse(ReadOnlySpan<byte> prof)
	{
		ColorProfile colorProfile = ColorProfile.Parse(prof);
		if (colorProfile is MatrixProfile matrixProfile)
		{
			return new MatrixProfileWrapper(matrixProfile);
		}
		else if (colorProfile is CurveProfile curveProfile)
		{
			return new CurveProfileWrapper(curveProfile);
		}
		else
		{
			return new ColorProfileWrapper(colorProfile);
		}
	}

	private abstract class ColorProfileWrapper<TProfile>
		: IColorProfile
		  where TProfile : ColorProfile
	{
		private readonly TProfile source;

		protected ColorProfileWrapper(TProfile source)
		{
			this.source = source;
		}

		protected TProfile Source => this.source;

		public bool IsValid => this.source.IsValid;

		public ReadOnlySpan<byte> ProfileBytes => this.source.ProfileBytes;

		public ColorProfileType ProfileType => this.source.ProfileType;

		public ProfileColorSpace DataColorSpace => this.source.DataColorSpace;

		public ProfileColorSpace PcsColorSpace => this.source.PcsColorSpace;

		public ColorProfile Get()
		{
			return this.source;
		}
	}

	private sealed class ColorProfileWrapper
		: ColorProfileWrapper<ColorProfile>
	{
		public ColorProfileWrapper(ColorProfile source)
			: base(source)
		{
		}
	}

	private sealed class CurveProfileWrapper
		: ColorProfileWrapper<CurveProfile>,
		  ICurveProfile
	{
		private readonly ProfileCurveWrapper profileCurve;

		public CurveProfileWrapper(CurveProfile source)
			: base(source)
		{
			this.profileCurve = new ProfileCurveWrapper(source.Curve);
		}

		public bool IsLinear => this.Source.IsLinear;

		public IProfileCurve Curve => this.profileCurve;
	}

	private sealed class MatrixProfileWrapper
		: ColorProfileWrapper<MatrixProfile>,
		  IMatrixProfile
	{
		private readonly ProfileCurveWrapper profileCurve;

		public MatrixProfileWrapper(MatrixProfile source)
			: base(source)
		{
			this.profileCurve = new ProfileCurveWrapper(source.Curve);
		}

		public bool IsLinear => this.Source.IsLinear;

		public IProfileCurve Curve => this.profileCurve;

		public Matrix4x4 Matrix => (Matrix4x4)this.Source.Matrix;

		public Matrix4x4 InverseMatrix => (Matrix4x4)this.Source.InverseMatrix;
	}

	private sealed class ProfileCurveWrapper
		: IProfileCurve
	{
		private readonly ColorProfile.ProfileCurve source;

		public ProfileCurveWrapper(ColorProfile.ProfileCurve source)
		{
			this.source = source;
		}

		public ReadOnlySpan<float> Gamma => this.source.Gamma;

		public ReadOnlySpan<float> InverseGamma => this.source.InverseGamma;
	}
}
