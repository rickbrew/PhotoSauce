// Copyright © Clinton Ingram and Contributors.  Licensed under the MIT License.

using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;

using PhotoSauce.MagicScaler;
using PhotoSauce.Interop.Libpng;
using static PhotoSauce.Interop.Libpng.Libpng;
using static System.Buffers.Binary.BinaryPrimitives;

namespace PhotoSauce.NativeCodecs.Libpng;

internal sealed unsafe class PngContainer : IImageContainer, IIccProfileSource, IExifSource
{
	private static readonly bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

	private readonly Stream stream;
	private readonly long streamStart;
	private ps_png_struct* handle;
	private ColorProfile? profile;
	private PngFrame? frame;

	private PngContainer(ps_png_struct* pinst, Stream stm, long pos, IDecoderOptions? _)
	{
		stream = stm;
		streamStart = pos;
		handle = pinst;
		frame = null;
	}

	public string MimeType => ImageMimeTypes.Png;

	public int FrameCount => 1;

	int IIccProfileSource.ProfileLength => getIccp().Length;

	int IExifSource.ExifLength => getExif().Length;

	public IImageFrame GetFrame(int index)
	{
		if (index != 0)
			throw new ArgumentOutOfRangeException(nameof(index));

		return frame ??= new PngFrame(this);
	}

	public static PngContainer? TryLoad(Stream imgStream, IDecoderOptions? options)
	{
		long pos = imgStream.Position;
		var handle = PngFactory.CreateDecoder();
		if (handle is null)
			ThrowHelper.ThrowOutOfMemory();

		var iod = handle->io_ptr;
		iod->stream_handle = GCHandle.ToIntPtr(GCHandle.Alloc(imgStream));
		iod->read_callback = pfnReadCallback;

		if (PngReadInfo(handle) == TRUE)
			return new PngContainer(handle, imgStream, pos, options);

		imgStream.Position = pos;
		GCHandle.FromIntPtr(handle->io_ptr->stream_handle).Free();
		PngDestroyRead(handle);

		return null;
	}

	public ps_png_struct* GetHandle()
	{
		if (handle is null)
			ThrowHelper.ThrowObjectDisposed(nameof(PngContainer));

		return handle;
	}

	public void CheckResult(int res)
	{
		if (res == FALSE)
			throwPngError(handle);
	}

	public void RewindStream() => stream.Position = streamStart;

	void IIccProfileSource.CopyProfile(Span<byte> dest) => getIccp().CopyTo(dest);

	void IExifSource.CopyExif(Span<byte> dest) => getExif().CopyTo(dest);

	[DoesNotReturn]
	private static void throwPngError(ps_png_struct* handle) =>
		throw new InvalidOperationException($"{nameof(Libpng)} decoder failed. {new string(PngGetLastError(handle))}");

	private ReadOnlySpan<byte> getIccp()
	{
		if (handle->HasChunk(PNG_INFO_iCCP))
		{
			byte* data = null;
			uint len = 0;
			PngGetIccp(handle, &data, &len);

			return new ReadOnlySpan<byte>(data, (int)len);
		}

		return (profile ??= generateIccProfile()).ProfileBytes;
	}

	private ReadOnlySpan<byte> getExif()
	{
		byte* data = null;
		uint len = 0;
		PngGetExif(handle, &data, &len);

		return new ReadOnlySpan<byte>(data, (int)len);
	}

	private ColorProfile generateIccProfile()
	{
		bool isGrey = ((PixelSource)frame!.PixelSource).Format.ColorRepresentation is PixelColorRepresentation.Grey;
		var prof = isGrey ? ColorProfile.sGrey : ColorProfile.sRGB;

		using var buff = BufferPool.RentLocal<byte>(prof.ProfileBytes.Length);
		var span = buff.Span;

		prof.ProfileBytes.CopyTo(span);
		span[84..100].Clear();
		WriteUInt32BigEndian(span[80..], 0x6D616763);

		var name = span[(isGrey ? 220 : 280)..];
		WriteUInt16BigEndian(name[0..], '.');
		WriteUInt16BigEndian(name[2..], 'P');
		WriteUInt16BigEndian(name[4..], 'N');
		WriteUInt16BigEndian(name[6..], 'G');

		if (handle->HasChunk(PNG_INFO_cHRM))
		{
			Debug.Assert(span.Length == 480);

			int wx, wy, rx, ry, gx, gy, bx, by;
			PngGetChrm(handle, &wx, &wy, &rx, &ry, &gx, &gy, &bx, &by);

			var wxyz = xyToXYZ(fromPngFixed(wx), fromPngFixed(wy));
			var rxyz = xyToXYZ(fromPngFixed(rx), fromPngFixed(ry));
			var gxyz = xyToXYZ(fromPngFixed(gx), fromPngFixed(gy));
			var bxyz = xyToXYZ(fromPngFixed(bx), fromPngFixed(by));

			var adapt = ConversionMatrix.GetChromaticAdaptation(wxyz);
			var mxyz = ConversionMatrix.GetRgbToXyz(rxyz, gxyz, bxyz, wxyz);
			var axyz = adapt * mxyz;

			var chad = span[352..];
			WriteInt32BigEndian(chad[ 0..], toS15Fixed16(adapt.M11));
			WriteInt32BigEndian(chad[ 4..], toS15Fixed16(adapt.M12));
			WriteInt32BigEndian(chad[ 8..], toS15Fixed16(adapt.M13));
			WriteInt32BigEndian(chad[12..], toS15Fixed16(adapt.M21));
			WriteInt32BigEndian(chad[16..], toS15Fixed16(adapt.M22));
			WriteInt32BigEndian(chad[20..], toS15Fixed16(adapt.M23));
			WriteInt32BigEndian(chad[24..], toS15Fixed16(adapt.M31));
			WriteInt32BigEndian(chad[28..], toS15Fixed16(adapt.M32));
			WriteInt32BigEndian(chad[32..], toS15Fixed16(adapt.M33));

			var rcol = span[396..];
			WriteInt32BigEndian(rcol[0..], toS15Fixed16(axyz.M11));
			WriteInt32BigEndian(rcol[4..], toS15Fixed16(axyz.M21));
			WriteInt32BigEndian(rcol[8..], toS15Fixed16(axyz.M31));

			var gcol = span[416..];
			WriteInt32BigEndian(gcol[0..], toS15Fixed16(axyz.M12));
			WriteInt32BigEndian(gcol[4..], toS15Fixed16(axyz.M22));
			WriteInt32BigEndian(gcol[8..], toS15Fixed16(axyz.M32));

			var bcol = span[436..];
			WriteInt32BigEndian(bcol[0..], toS15Fixed16(axyz.M13));
			WriteInt32BigEndian(bcol[4..], toS15Fixed16(axyz.M23));
			WriteInt32BigEndian(bcol[8..], toS15Fixed16(axyz.M33));
		}

		if (handle->HasChunk(PNG_INFO_gAMA))
		{
			Debug.Assert(span.Length == (isGrey ? 360 : 480));

			int gama;
			PngGetGama(handle, &gama);

			span = span[..^16];
			WriteUInt32BigEndian(span, (uint)span.Length);

			var trc = span[(isGrey ? 188 : 224)..];
			WriteUInt32BigEndian(trc[0..], 16);
			if (!isGrey)
			{
				WriteUInt32BigEndian(trc[12..], 16);
				WriteUInt32BigEndian(trc[24..], 16);
			}

			var para = span[(isGrey ? 336 : 456)..];
			WriteUInt16BigEndian(para[0..], 0);
			WriteInt32BigEndian(para[4..], toS15Fixed16(1 / fromPngFixed(gama)));
		}

		return ColorProfile.Parse(span);

		static double fromPngFixed(int val) => val / 100000d;
		static int toS15Fixed16(double val) => (int)Math.Round(val * 65536);
		static Vector3C xyToXYZ(double x, double y) => new(x / y, 1, (1 - x - y) / y);
	}

	private void dispose(bool disposing)
	{
		if (handle is null)
			return;

		GCHandle.FromIntPtr(handle->io_ptr->stream_handle).Free();
		PngDestroyRead(handle);
		handle = null;

		if (disposing)
			GC.SuppressFinalize(this);
	}

	public void Dispose() => dispose(true);

	~PngContainer()
	{
		ThrowHelper.ThrowIfFinalizerExceptionsEnabled(nameof(PngContainer));

		dispose(false);
	}

#if !NET5_0_OR_GREATER
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint ReadCallback(nint pinst, byte* buff, nuint cb);
	private static readonly ReadCallback delReadCallback = typeof(PngContainer).CreateMethodDelegate<ReadCallback>(nameof(readCallback));
#else
	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	static
#endif
	private nuint readCallback(nint pinst, byte* buff, nuint cb)
	{
		try
		{
			var stm = Unsafe.As<Stream>(GCHandle.FromIntPtr(pinst).Target!);
			cb = (uint)stm.Read(new Span<byte>(buff, checked((int)cb)));

			return cb;
		}
		catch when (!isWindows)
		{
			return unchecked((nuint)~0ul);
		}
	}

	private static readonly delegate* unmanaged[Cdecl]<nint, byte*, nuint, nuint> pfnReadCallback =
#if NET5_0_OR_GREATER
		&readCallback;
#else
		(delegate* unmanaged[Cdecl]<nint, byte*, nuint, nuint>)Marshal.GetFunctionPointerForDelegate(delReadCallback);
#endif
}

internal sealed unsafe class PngFrame : IImageFrame, IMetadataSource
{
	public readonly PngContainer container;

	private bool interlace;
	private int width, height;
	private PixelFormat format;
	private FrameBufferSource? frameBuff;
	private RentedBuffer<byte> lineBuff;

	public IPixelSource PixelSource { get; }

	public PngFrame(PngContainer cont)
	{
		container = cont;
		PixelSource = new PngPixelSource(this);

		setupDecoder(cont.GetHandle());
	}

	[MemberNotNull(nameof(format))]
	private void setupDecoder(ps_png_struct* handle)
	{
		uint w, h;
		int bpc, color, ilace;
		container.CheckResult(PngGetIhdr(handle, &w, &h, &bpc, &color, &ilace));

		(width, height) = ((int)w, (int)h);
		interlace = ilace != PNG_INTERLACE_NONE;

		bool hasTrns = handle->HasChunk(PNG_INFO_tRNS);
		bool hasAlpha = (color & PNG_COLOR_MASK_ALPHA) != 0 || hasTrns;
		bool hasColor = (color & PNG_COLOR_MASK_COLOR) != 0;
		format = hasAlpha ? PixelFormat.Rgba32 : hasColor ? PixelFormat.Rgb24 : PixelFormat.Grey8;

		if (bpc < 8 || hasTrns || (color & PNG_COLOR_MASK_PALETTE) != 0)
			container.CheckResult(PngSetExpand(handle));

		if (hasAlpha && !hasColor)
			container.CheckResult(PngSetGrayToRgb(handle));

		if (bpc == 16)
			container.CheckResult(PngSetStrip16(handle));

		if (interlace)
			container.CheckResult(PngSetInterlaceHandling(handle));

		container.CheckResult(PngReadUpdateInfo(handle));
	}

	public bool TryGetMetadata<T>([NotNullWhen(true)] out T? metadata) where T : IMetadata
	{
		var handle = container.GetHandle();

		if (typeof(T) == typeof(ResolutionMetadata) && handle->HasChunk(PNG_INFO_pHYs))
		{
			uint xres, yres;
			int unit;
			PngGetPhys(handle, &xres, &yres, &unit);

			metadata = (T)(object)(new ResolutionMetadata(new Rational(xres, 1), new Rational(yres, 1), unit == PNG_RESOLUTION_METER ? ResolutionUnit.Meter : ResolutionUnit.Virtual));
			return true;
		}

		if (typeof(T) == typeof(IIccProfileSource) && (handle->HasChunk(PNG_INFO_iCCP) || handle->HasChunk(PNG_INFO_cHRM) || handle->HasChunk(PNG_INFO_gAMA)))
		{
			metadata = (T)(object)container;
			return true;
		}

		if (typeof(T) == typeof(IExifSource) && handle->HasChunk(PNG_INFO_eXIf))
		{
			metadata = (T)(object)container;
			return true;
		}

		metadata = default;
		return false;
	}

	public void ResetDecoder()
	{
		var handle = container.GetHandle();

		container.RewindStream();
		container.CheckResult(PngResetRead(handle));
		container.CheckResult(PngReadInfo(handle));

		setupDecoder(handle);
	}

	public void Dispose()
	{
		frameBuff?.Dispose();
		lineBuff.Dispose();
	}

	private class PngPixelSource : PixelSource, IFramePixelSource
	{
		private int lastRow;

		public PngFrame frame;

		public override PixelFormat Format => frame.format;
		public override int Width => frame.width;
		public override int Height => frame.height;
		public IImageFrame Frame => frame;

		public PngPixelSource(PngFrame frm) => frame = frm;

		private FrameBufferSource getFrameBuffer()
		{
			if (frame.frameBuff is null)
			{
				var container = frame.container;
				var handle = container.GetHandle();

				var fbuf = new FrameBufferSource(Width, Height, Format);
				fixed (byte* pbuf = fbuf.Span)
				{
					using var lines = BufferPool.RentLocal<nint>(Height);
					var lspan = lines.Span;

					for (int i = 0; i < lspan.Length; i++)
						lspan[i] = (nint)(pbuf + i * fbuf.Stride);

					fixed (nint* plines = lines)
						container.CheckResult(PngReadImage(handle, (byte**)plines));
				}

				frame.frameBuff = fbuf;
			}

			return frame.frameBuff;
		}

		protected override void CopyPixelsInternal(in PixelArea prc, int cbStride, int cbBufferSize, byte* pbBuffer)
		{
			if (frame.interlace)
			{
				getFrameBuffer().CopyPixels(prc, cbStride, cbBufferSize, pbBuffer);
				return;
			}

			if (prc.Y < lastRow)
			{
				frame.ResetDecoder();
				lastRow = 0;
			}

			var container = frame.container;
			var handle = container.GetHandle();
			int bpp = Format.BytesPerPixel;

			var linebuff = Span<byte>.Empty;
			if (prc.Width < Width)
			{
				if (frame.lineBuff.IsEmpty)
					frame.lineBuff = BufferPool.Rent<byte>(Width * bpp);

				linebuff = frame.lineBuff.Span;
			}

			fixed (byte* pbuff = linebuff)
			{
				for (int y = lastRow; y < prc.Y; y++)
				{
					container.CheckResult(PngReadRow(handle, pbuff is null ? pbBuffer : pbuff));
					lastRow++;
				}

				for (int y = 0; y < prc.Height; y++)
				{
					byte* pout = pbBuffer + cbStride * y;
					if (pbuff is null)
					{
						container.CheckResult(PngReadRow(handle, pout));
					}
					else
					{
						container.CheckResult(PngReadRow(handle, pbuff));
						Unsafe.CopyBlockUnaligned(pout, pbuff + prc.X * bpp, (uint)(prc.Width * bpp));
					}

					lastRow++;
				}
			}
		}

		public override string ToString() => nameof(PngPixelSource);
	}
}
