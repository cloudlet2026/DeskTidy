using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeskTidy.Core;

/// <summary>
/// 按文件扩展名把桌面文件归类到若干个"格子"。
/// 规则与酷呆桌面/腾讯桌面整理类似：文档、图片、视频、音频、压缩包、程序、其他。
/// </summary>
public static class FileClassifier
{
    public const string BoxDocuments = "文档";
    public const string BoxImages    = "图片";
    public const string BoxVideos    = "视频";
    public const string BoxMusic     = "音乐";
    public const string BoxArchives  = "压缩包";
    public const string BoxPrograms  = "程序";
    public const string BoxOthers    = "其他";

    private static readonly Dictionary<string, string> ExtMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // 文档
        [".txt"]  = BoxDocuments, [".doc"]  = BoxDocuments, [".docx"] = BoxDocuments,
        [".xls"]  = BoxDocuments, [".xlsx"] = BoxDocuments, [".ppt"]  = BoxDocuments,
        [".pptx"] = BoxDocuments, [".pdf"]  = BoxDocuments, [".rtf"]  = BoxDocuments,
        [".csv"]  = BoxDocuments, [".md"]   = BoxDocuments, [".wps"]  = BoxDocuments,
        [".et"]   = BoxDocuments, [".dps"]  = BoxDocuments,
        // 图片
        [".jpg"]  = BoxImages, [".jpeg"] = BoxImages, [".png"]  = BoxImages,
        [".gif"]  = BoxImages, [".bmp"]  = BoxImages, [".webp"] = BoxImages,
        [".svg"]  = BoxImages, [".ico"]  = BoxImages, [".tiff"] = BoxImages,
        // 视频
        [".mp4"]  = BoxVideos, [".avi"]  = BoxVideos, [".mkv"]  = BoxVideos,
        [".mov"]  = BoxVideos, [".wmv"]  = BoxVideos, [".flv"]  = BoxVideos,
        [".rmvb"] = BoxVideos, [".m4v"]  = BoxVideos,
        // 音乐
        [".mp3"]  = BoxMusic, [".wav"]  = BoxMusic, [".flac"] = BoxMusic,
        [".aac"]  = BoxMusic, [".m4a"]  = BoxMusic, [".wma"]  = BoxMusic,
        // 压缩包
        [".zip"]  = BoxArchives, [".rar"] = BoxArchives, [".7z"]  = BoxArchives,
        [".tar"]  = BoxArchives, [".gz"]  = BoxArchives, [".bz2"] = BoxArchives,
        // 程序 / 快捷方式
        [".exe"]  = BoxPrograms, [".msi"] = BoxPrograms, [".lnk"]  = BoxPrograms,
        [".bat"]  = BoxPrograms, [".cmd"] = BoxPrograms, [".ps1"]  = BoxPrograms,
    };

    public static string Classify(string path)
    {
        if (Directory.Exists(path)) return BoxOthers;
        var ext = Path.GetExtension(path);
        return ext != null && ExtMap.TryGetValue(ext, out var box) ? box : BoxOthers;
    }

    public static string[] AllBoxNames { get; } =
        { BoxDocuments, BoxImages, BoxVideos, BoxMusic, BoxArchives, BoxPrograms, BoxOthers };
}
