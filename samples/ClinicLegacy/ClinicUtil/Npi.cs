namespace ClinicUtil
{
    /// <summary>National Provider Identifier checks: ten digits, Luhn check digit over the prefix 80840.</summary>
    public static class Npi
    {
        public static bool IsValid(string npi)
        {
            if (npi == null || npi.Length != 10) return false;
            int sum = 24;
            for (int i = 8; i >= 0; i--)
            {
                int d = npi[i] - '0';
                if (d < 0 || d > 9) return false;
                if ((8 - i) % 2 == 0)
                {
                    d *= 2;
                    if (d > 9) d -= 9;
                }
                sum += d;
            }
            int check = (10 - sum % 10) % 10;
            return npi[9] - '0' == check;
        }
    }
}
