using System.Text;

namespace SqlMcp.Introspection;

/// <summary>
/// The name pattern agents pass to <c>list_objects</c>: <c>*</c> matches any run of characters and <c>?</c> one
/// character (spec: filtering the object list).
/// </summary>
internal static class Glob
{
    /// <summary>The escape character the <c>LIKE</c> pattern is written for: use it as <c>ESCAPE '\'</c>.</summary>
    public const char LikeEscape = '\\';

    /// <summary>
    /// Translates a glob into a T-SQL <c>LIKE</c> pattern (design I4). <c>LIKE</c>'s own wildcards, <c>%</c>,
    /// <c>_</c> and <c>[</c>, and the escape character itself, are escaped first, so they match literally.
    /// </summary>
    public static string ToLike(string glob)
    {
        var like = new StringBuilder(glob.Length + 8);
        foreach (var c in glob)
        {
            switch (c)
            {
                case '*':
                    like.Append('%');
                    break;
                case '?':
                    like.Append('_');
                    break;
                case '%' or '_' or '[' or LikeEscape:
                    like.Append(LikeEscape).Append(c);
                    break;
                default:
                    like.Append(c);
                    break;
            }
        }

        return like.ToString();
    }
}
