using System;
using System.Threading.Tasks;

namespace BetterPetitPlanet.Core.Config;

public interface IConfigService
{
    AppConfig Config { get; }
    Task InitializeAsync() => Task.CompletedTask;
    void Save();
    Task SaveAsync() => Task.Run(Save);
    // Persist a detached candidate before publishing its editable fields to Config.
    Task SaveMusicOverlayAsync(MusicOverlayConfig settings) =>
        Task.FromException(new NotSupportedException("当前配置服务不支持谱面设置事务保存"));
    void Reload();
}
