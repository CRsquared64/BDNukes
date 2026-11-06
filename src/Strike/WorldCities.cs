namespace BDNukes
{
    /// <summary>
    /// Major real-world cities, offered as strike planner targets on a body called Earth (Real Solar System, Sol).
    /// City-centre coordinates, degrees north and east.
    /// </summary>
    internal static class WorldCities
    {
        internal struct City { public string name; public double lat, lon; }

        static City C(string n, double lat, double lon) => new City { name = n, lat = lat, lon = lon };

        internal static readonly City[] All =
        {
            // North America
            C("Washington, D.C.", 38.8951, -77.0364), C("New York", 40.7128, -74.0060), C("Los Angeles", 34.0522, -118.2437),
            C("Chicago", 41.8781, -87.6298), C("Houston", 29.7604, -95.3698), C("San Francisco", 37.7749, -122.4194),
            C("Seattle", 47.6062, -122.3321), C("Miami", 25.7617, -80.1918), C("Denver", 39.7392, -104.9903),
            C("Boston", 42.3601, -71.0589), C("Atlanta", 33.7490, -84.3880), C("Las Vegas", 36.1699, -115.1398),
            C("Honolulu", 21.3069, -157.8583), C("Anchorage", 61.2181, -149.9003), C("Toronto", 43.6532, -79.3832),
            C("Montreal", 45.5019, -73.5674), C("Vancouver", 49.2827, -123.1207), C("Ottawa", 45.4215, -75.6972),
            C("Calgary", 51.0447, -114.0719), C("Mexico City", 19.4326, -99.1332), C("Havana", 23.1136, -82.3666),
            // South America
            C("Sao Paulo", -23.5505, -46.6333), C("Rio de Janeiro", -22.9068, -43.1729), C("Brasilia", -15.7939, -47.8828),
            C("Buenos Aires", -34.6037, -58.3816), C("Santiago", -33.4489, -70.6693), C("Lima", -12.0464, -77.0428),
            C("Bogota", 4.7110, -74.0721), C("Caracas", 10.4806, -66.9036),
            // Europe
            C("London", 51.5074, -0.1278), C("Paris", 48.8566, 2.3522), C("Berlin", 52.5200, 13.4050),
            C("Madrid", 40.4168, -3.7038), C("Rome", 41.9028, 12.4964), C("Moscow", 55.7558, 37.6173),
            C("Saint Petersburg", 59.9311, 30.3609), C("Kyiv", 50.4501, 30.5234), C("Warsaw", 52.2297, 21.0122),
            C("Vienna", 48.2082, 16.3738), C("Prague", 50.0755, 14.4378), C("Amsterdam", 52.3676, 4.9041),
            C("Brussels", 50.8503, 4.3517), C("Stockholm", 59.3293, 18.0686), C("Oslo", 59.9139, 10.7522),
            C("Helsinki", 60.1699, 24.9384), C("Copenhagen", 55.6761, 12.5683), C("Dublin", 53.3498, -6.2603),
            C("Edinburgh", 55.9533, -3.1883), C("Lisbon", 38.7223, -9.1393), C("Athens", 37.9838, 23.7275),
            C("Istanbul", 41.0082, 28.9784), C("Budapest", 47.4979, 19.0402), C("Bucharest", 44.4268, 26.1025),
            C("Reykjavik", 64.1466, -21.9426), C("Murmansk", 68.9585, 33.0827),
            // Africa and the Middle East
            C("Cairo", 30.0444, 31.2357), C("Lagos", 6.5244, 3.3792), C("Johannesburg", -26.2041, 28.0473),
            C("Cape Town", -33.9249, 18.4241), C("Nairobi", -1.2921, 36.8219), C("Kinshasa", -4.4419, 15.2663),
            C("Casablanca", 33.5731, -7.5898), C("Addis Ababa", 9.0300, 38.7400), C("Riyadh", 24.7136, 46.6753),
            C("Dubai", 25.2048, 55.2708), C("Tehran", 35.6892, 51.3890), C("Baghdad", 33.3152, 44.3661),
            C("Jerusalem", 31.7683, 35.2137),
            // Asia
            C("Tokyo", 35.6762, 139.6503), C("Osaka", 34.6937, 135.5023), C("Seoul", 37.5665, 126.9780),
            C("Pyongyang", 39.0392, 125.7625), C("Beijing", 39.9042, 116.4074), C("Shanghai", 31.2304, 121.4737),
            C("Hong Kong", 22.3193, 114.1694), C("Taipei", 25.0330, 121.5654), C("Manila", 14.5995, 120.9842),
            C("Bangkok", 13.7563, 100.5018), C("Hanoi", 21.0278, 105.8342), C("Singapore", 1.3521, 103.8198),
            C("Jakarta", -6.2088, 106.8456), C("Delhi", 28.6139, 77.2090), C("Mumbai", 19.0760, 72.8777),
            C("Kolkata", 22.5726, 88.3639), C("Karachi", 24.8607, 67.0011), C("Islamabad", 33.6844, 73.0479),
            C("Dhaka", 23.8103, 90.4125), C("Novosibirsk", 55.0084, 82.9357), C("Vladivostok", 43.1155, 131.8855),
            C("Ulaanbaatar", 47.8864, 106.9057),
            // Oceania
            C("Sydney", -33.8688, 151.2093), C("Melbourne", -37.8136, 144.9631), C("Canberra", -35.2809, 149.1300),
            C("Perth", -31.9505, 115.8605), C("Auckland", -36.8485, 174.7633), C("Wellington", -41.2866, 174.7756),
        };
    }
}
