using Avalonia.Media.Imaging;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Custom;
using MFAToolsPlus.Helper;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace MFAToolsPlus.Extensions.MaaFW.Custom;


public class MFAOCRRecognition : IMaaCustomRecognition
{
    public string Name { get; set; } = nameof(MFAOCRRecognition);
    public static Bitmap? Bitmap { get; set; } = null;
    public static string? Output { get; set; } = null;
    public bool Analyze<T>(T context, in AnalyzeArgs args, in AnalyzeResults results) where T : IMaaContext
    {
        if (Bitmap == null)
            return false;
        using var image = new MaaImageBuffer();
        image.TrySetEncodedData(RecognitionHelper.BitmapToBytes(Bitmap));
        var pipeline = RecognitionHelper.BuildAppendOcrPayload(args.Roi.X, args.Roi.Y, args.Roi.Width, args.Roi.Height);

        var detail = context.RunRecognition(
            "AppendOCR",
            image,pipeline);

        if (detail != null)
        {
            var query = JsonConvert.DeserializeObject<RecognitionHelper.RecognitionQuery>(detail.Detail);
            if (!string.IsNullOrWhiteSpace(query?.Best?.Text))
                Output = query.Best.Text;
        }
        else
        {
            ToastHelper.Error("识别失败！");
        }

        return true;
    }
}
