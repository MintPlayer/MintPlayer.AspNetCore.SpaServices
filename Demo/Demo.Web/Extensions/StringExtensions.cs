using System.Text.RegularExpressions;

namespace Demo.Web.Extensions;

public static class StringExtensions
{
	public static string Slugify(this string s)
	{
		var result = s.ToLower();
		result = Regex.Replace(result, @"\s+", "-");
		result = result.Normalize(System.Text.NormalizationForm.FormD);
		result = Regex.Replace(result, @"[\u0300-\u036f]", "");
		result = Regex.Replace(result, @"[^\w\-]+", "");
		result = Regex.Replace(result, @"\-\-+", "");
		result = Regex.Replace(result, @"^-+", "");
		result = Regex.Replace(result, @"/-+$", "");
		return result;
	}
}
