using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// Oppilas, PelinNimi, Repo, Lista checkoutattavista tiedostoista/kansioista, Solutiontiedosto
class GameRecord : ICloneable
{
    public object Clone()
    {
        return this.MemberwiseClone();
    }
    public string PupilGroupName;
    public string GameName;
    public string SVNRepo;
    public List<string> toProcess;
    public string Solution;
    public string ContentFolder;
    public string TemplateFolder;
}

class ContentTool
{
    public string ToolExe = "";
    public string TemplateFile = "";
    public string ContentExt = "";
    public string ContentSubfolder = "";
    public string ContentDescription = "";
    public string FileToEditOverride= "";
}

class ExternalExecutableSettings
{
    // Visual C# exe
    public string VCSExePath;
    // MS build system exe
    public string MsbuildExePath;

    //@"C:\Users\opetus01\Downloads\svn-win32-1.8.8\svn-win32-1.8.8\bin\svn.exe"; OR @"C:\Program Files\TortoiseSVN\bin\svn.exe";
    public string SvnCliExePath;
    // Can be used to pass username and password
    public string SvnOptions;
    // If for some reason the password cannot be given using the options (some idiotic security measures may prevent this),
    //  using this will try to do it in the interactive mode as if there was actual user typing it in. However, prefer to leave it empty.
    public string SvnInteractivePassword;
}

enum Task
{
    Checkout,
    UpdateListed,
    Compile,
    RunGame,
    RunTool,
    CheckForModified,
    ChooseFromModified,
    AddListed,
    CommitListed,
    None
}

enum Status
{
    Wait,
    OK,
    Fail,
    NA
}
