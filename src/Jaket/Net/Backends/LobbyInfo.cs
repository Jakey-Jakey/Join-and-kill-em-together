namespace Jaket.Net.Backends;

/// <summary> Entry of the public lobby browser. </summary>
public class LobbyInfo
{
    /// <summary> Name of the lobby. </summary>
    public string Name;
    /// <summary> Mission loaded by the lobby at the moment. </summary>
    public string Level;
    /// <summary> Current number of members. </summary>
    public int MemberCount;
    /// <summary> Maximum number of members. </summary>
    public int MaxMembers;
    /// <summary> Joins the lobby. </summary>
    public Runnable Join;
}
