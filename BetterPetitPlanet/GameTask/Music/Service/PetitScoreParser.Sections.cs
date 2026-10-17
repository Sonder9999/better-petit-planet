using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed partial class PetitScoreParser
{
    public static List<SongSectionMarker> ExtractSections(List<PerformanceEvent> events, int totalDurationMs)
    {
        if (events == null || events.Count == 0) return [];

        var sections = new List<SongSectionMarker>();
        int totalNotes = events.Count;

        // 1. 扫描曲谱中显式标注的段落元数据
        for (int i = 0; i < events.Count; i++)
        {
            var lyric = events[i].Lyric?.Trim();
            if (string.IsNullOrWhiteSpace(lyric)) continue;

            if (TryParseSection(lyric, out var sectionType, out var name, out var shortName))
            {
                // 避免同一类型的段落在极近的音符内被重复添加
                if (sections.Count > 0 && sections[^1].SectionType == sectionType && Math.Abs(i - sections[^1].NoteIndex) < 4)
                {
                    continue;
                }

                double progress = totalNotes > 1 ? (double)i / (totalNotes - 1) : 0.0;
                sections.Add(new SongSectionMarker
                {
                    Name = name,
                    ShortName = shortName,
                    SectionType = sectionType,
                    NoteIndex = i,
                    TimeMs = events[i].TimeMs,
                    NormalizedProgress = Math.Clamp(progress, 0.0, 1.0)
                });
            }
        }

        // 2. 检查首端是否已有前奏，若没有且歌曲至少有 10 个音符，默认将第 0 个音符标记为前奏起点
        if (sections.Count == 0 || (sections[0].NoteIndex > 3 && sections[0].SectionType != SongSectionType.Intro))
        {
            sections.Insert(0, new SongSectionMarker
            {
                Name = "Intro",
                ShortName = "Intro",
                SectionType = SongSectionType.Intro,
                NoteIndex = 0,
                TimeMs = events[0].TimeMs,
                NormalizedProgress = 0.0
            });
        }

        // 3. 检查是否包含高潮段（Chorus），若没有显式高潮标注，通过“音符密度峰值算法”自动探测高潮段落
        bool hasChorus = sections.Any(s => s.SectionType == SongSectionType.Chorus);
        if (!hasChorus && totalNotes >= 20)
        {
            var detectedClimaxes = DetectClimaxSections(events);
            foreach (var climax in detectedClimaxes)
            {
                if (!sections.Any(s => Math.Abs(s.NoteIndex - climax.NoteIndex) < 4))
                {
                    sections.Add(climax);
                }
            }
            sections = sections.OrderBy(s => s.NoteIndex).ToList();
        }

        // 4. 重新规整进度比例
        for (int i = 0; i < sections.Count; i++)
        {
            sections[i].NormalizedProgress = totalNotes > 1 ? (double)sections[i].NoteIndex / (totalNotes - 1) : 0.0;
        }

        return sections;
    }

    private static bool TryParseSection(string lyric, out SongSectionType type, out string name, out string shortName)
    {
        name = lyric.Trim();
        shortName = lyric.Trim();
        type = SongSectionType.Verse;

        string lower = lyric.ToLowerInvariant();

        // Intro: intro, prelude, introduction, 前奏
        if (lower.Contains("intro") || lower.Contains("prelude") || lower.Contains("前奏"))
        {
            type = SongSectionType.Intro;
            return true;
        }

        // Pre-Chorus: pre-chorus, prechorus, 导歌, 预备段
        if (lower.Contains("pre-chorus") || lower.Contains("prechorus") || lower.Contains("导歌") || lower.Contains("预备段"))
        {
            type = SongSectionType.Verse;
            return true;
        }

        // Post-Chorus: post-chorus, postchorus
        if (lower.Contains("post-chorus") || lower.Contains("postchorus"))
        {
            type = SongSectionType.Bridge;
            return true;
        }

        // Climax / Chorus / Drop: chorus, climax, drop, hook, 高潮, 副歌, refrain
        if (lower.Contains("chorus") || lower.Contains("climax") || lower.Contains("drop") ||
            lower.Contains("hook") || lower.Contains("高潮") || lower.Contains("副歌") || lower.Contains("refrain"))
        {
            type = SongSectionType.Chorus;
            return true;
        }

        // Verse: verse, 主歌, theme, stanza
        if (lower.Contains("verse") || lower.Contains("主歌") || lower.Contains("theme") || lower.Contains("stanza"))
        {
            type = SongSectionType.Verse;
            return true;
        }

        // Bridge / Interlude: bridge, interlude, 间奏, 桥段, 过渡, solo, rap, break
        if (lower.Contains("bridge") || lower.Contains("interlude") || lower.Contains("间奏") ||
            lower.Contains("桥段") || lower.Contains("solo") || lower.Contains("rap") || lower.Contains("break"))
        {
            type = SongSectionType.Bridge;
            return true;
        }

        // Outro: outro, ending, coda, 尾奏, 尾声, fine, final chord
        if (lower.Contains("outro") || lower.Contains("ending") || lower.Contains("coda") ||
            lower.Contains("尾奏") || lower.Contains("尾声") || lower.Contains("fine") || lower.Contains("final chord"))
        {
            type = SongSectionType.Outro;
            return true;
        }

        return false;
    }

    private static int ExtractTrailingNumber(string text)
    {
        var match = Regex.Match(text, @"(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var n))
        {
            return n;
        }
        return 0;
    }

    private static List<SongSectionMarker> DetectClimaxSections(List<PerformanceEvent> events)
    {
        var result = new List<SongSectionMarker>();
        int total = events.Count;
        if (total < 20) return result;

        // 计算滑动窗口音符密度（窗口大小 12 音符，步长 2 音符）
        int windowSize = Math.Min(15, total / 3);
        int startSearch = (int)(total * 0.20);
        int endSearch = (int)(total * 0.85);

        if (endSearch <= startSearch + windowSize) return result;

        double maxDensity = 0;
        int peakIndex = -1;

        for (int i = startSearch; i <= endSearch - windowSize; i += 2)
        {
            int duration = events[i + windowSize - 1].TimeMs - events[i].TimeMs;
            if (duration <= 0) continue;

            double density = (double)windowSize / (duration / 1000.0);
            if (density > maxDensity)
            {
                maxDensity = density;
                peakIndex = i;
            }
        }

        if (peakIndex >= 0)
        {
            // 若曲目较长（>= 120 音符），寻找第二高潮峰值
            int peakIndex2 = -1;
            double maxDensity2 = 0;

            if (total >= 120)
            {
                for (int i = startSearch; i <= endSearch - windowSize; i += 2)
                {
                    if (Math.Abs(i - peakIndex) < 35) continue;
                    int duration = events[i + windowSize - 1].TimeMs - events[i].TimeMs;
                    if (duration <= 0) continue;

                    double density = (double)windowSize / (duration / 1000.0);
                    if (density > maxDensity2)
                    {
                        maxDensity2 = density;
                        peakIndex2 = i;
                    }
                }
            }

            if (peakIndex2 >= 0 && maxDensity2 >= maxDensity * 0.70)
            {
                int firstIdx = Math.Min(peakIndex, peakIndex2);
                int secondIdx = Math.Max(peakIndex, peakIndex2);
                result.Add(new SongSectionMarker
                {
                    Name = "Chorus 1",
                    ShortName = "Chorus 1",
                    SectionType = SongSectionType.Chorus,
                    NoteIndex = firstIdx,
                    TimeMs = events[firstIdx].TimeMs,
                    NormalizedProgress = (double)firstIdx / (total - 1)
                });
                result.Add(new SongSectionMarker
                {
                    Name = "Chorus 2",
                    ShortName = "Chorus 2",
                    SectionType = SongSectionType.Chorus,
                    NoteIndex = secondIdx,
                    TimeMs = events[secondIdx].TimeMs,
                    NormalizedProgress = (double)secondIdx / (total - 1)
                });
            }
            else
            {
                result.Add(new SongSectionMarker
                {
                    Name = "Chorus",
                    ShortName = "Chorus",
                    SectionType = SongSectionType.Chorus,
                    NoteIndex = peakIndex,
                    TimeMs = events[peakIndex].TimeMs,
                    NormalizedProgress = (double)peakIndex / (total - 1)
                });
            }
        }

        return result;
    }
}

