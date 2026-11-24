using System;

namespace ClinicUtil
{
    /// <summary>Ages as the front desk counts them: whole years on the visit date.</summary>
    public static class AgeCalculator
    {
        public const int AdultAge = 18;

        public static int Age(DateTime birthDate) => Age(birthDate, DateTime.Today);

        public static int Age(DateTime birthDate, DateTime on)
        {
            var age = on.Year - birthDate.Year;
            if (birthDate.Date > on.AddYears(-age)) age--;
            return age;
        }

        public static bool IsMinor(DateTime birthDate) => Age(birthDate) < AdultAge;

        // Was used by the old intake screen, which was retired.
        public static string AgeBand(DateTime birthDate)
        {
            var age = Age(birthDate);
            if (age < 13) return "CHILD";
            if (age < AdultAge) return "ADOLESCENT";
            if (age < 65) return "ADULT";
            return "SENIOR";
        }
    }
}
