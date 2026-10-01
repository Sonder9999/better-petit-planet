namespace BetterPetitPlanet.Core.Config;

public interface IConfigService
{
    AppConfig Config { get; }
    void Save();
    void Reload();
}
