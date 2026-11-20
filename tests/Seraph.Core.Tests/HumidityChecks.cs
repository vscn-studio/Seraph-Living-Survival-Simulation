using Seraph.Core.Framework;

internal static class HumidityChecks
{
    internal static void Weather()
    {
        var dry = HumidityModel.RelativeHumidity(.4, 0, 20, 20);
        var rain = HumidityModel.RelativeHumidity(.4, 1, 20, 20);
        var heated = HumidityModel.RelativeHumidity(.4, 0, 20, 30);
        var cooled = HumidityModel.RelativeHumidity(.4, 0, 20, -20);
        if (!(dry > 0 && rain > dry && heated < dry && cooled <= 1
            && double.IsNaN(HumidityModel.RelativeHumidity(double.NaN, 0, 20, 20))))
            throw new Exception("Humidity must respond to weather, temperature and invalid inputs.");
    }
}
