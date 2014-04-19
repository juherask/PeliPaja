using System;
using System.Collections.Generic;
using Jypeli;
using Jypeli.Controls;
using Jypeli.Widgets;
using Microsoft.Xna.Framework.Graphics;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
//using System.Windows.Forms;

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
    public string ToolExe;
    public string TemplateFile;
    public string ContentExt;
    public string ContentDescription;
}

class User32
{
    [DllImport("user32.dll")]
    public static extern void SetWindowPos(uint Hwnd, int Level, int X, int Y, int W, int H, uint Flags);
}

/*
* For checking out we use
* > svn checkout <repo> <author_folder> --depth empty
* > cd <author_folder>/trunk
* > svn up <files/folders_you_want>
*/
public class WorkshopClient : Game
{
    static int HWND_TOP = 0;
    static int HWND_TOPMOST = -1;
    static int PROCESS_CHECK_INTERVAL = 100;

    // TODO: replace these with the ones loaded from an ini file
    string SVN_CLI_EXE = @"C:\Users\opetus01\Downloads\svn-win32-1.8.8\svn-win32-1.8.8\bin\svn.exe";
    string MSBUILD_EXE = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MsBuild.exe";
    string SVN_OPTIONS = @"--username 'jypeliworkshop'";
    string SVN_PASSWD = "qwerty12345";

    string CONTENT_EXT = ".png";
    string CONTENT_SUBFOLDER = "Content";

    Queue<Tuple<GameRecord, Task>> taskQueue;
    GameRecord workshopGame;
    ContentTool tool;
    Process activeCliProcess;
    string fileToEdit;

    bool processing = false;
    bool paused = false;
    bool topmost = false;
    bool wasTopmost = false;

    // Multithreading support
    Mutex stateQueueMutex = new Mutex();
    Queue<string> messageQueue = new Queue<string>();
    Queue<Tuple<GameRecord, Task, Status>> stateQueue = new Queue<Tuple<GameRecord, Task, Status>>();
    Thread processingThread;

    // Quickhack to keep track of changed files.
    List<string> newAndModifiedFileNames = new List<string>();
    List<char> newAndModifiedFileStates = new List<char>();

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

    Dictionary<Task, string> taskToLabel = new Dictionary<Task, string>()
    {
        {Task.Checkout, "Nouto"},
        {Task.UpdateListed, "Update"},
        {Task.Compile, "Kääntäminen"},
        {Task.RunGame, "Pelin ajo"},
    };

    public override void Begin()
    {
        //SetWindowSize(800, 600);
        SetWindowTopmost(topmost);
        Level.Background.Color = Color.White;

        IsMouseVisible = true;

        PhoneBackButton.Listen(ConfirmExit, "Lopeta peli");
        Keyboard.Listen(Key.Escape, Jypeli.ButtonState.Pressed, ConfirmExit, "Lopeta peli");
        Keyboard.Listen(Key.P, Jypeli.ButtonState.Pressed, PauseProcess, "Pistä tauolle");
        Keyboard.Listen(Key.T, Jypeli.ButtonState.Pressed, ToggleTopmost, "Tuo päällimmäiseksi");
        Exiting += StopThread;

        ReadGameInfoAndSettings();

        taskQueue = new Queue<Tuple<GameRecord, Task>>();

        GameObject logo = new GameObject(LoadImage("logo"));
        logo.Y = Screen.Height / 6*2;
        Add(logo);

        // Create buttons
        PushButton playGameButton = new PushButton(400, 50, "Pelaa uusinta versiota pelistä");
        // TODO: Nämä voisi lukea ini tiedostosta ja niitä voisi olla monta? Esim. "piirrä uusi kartta" "piirrä uusi pelihahmo" jne.
        PushButton createContentButton = new PushButton(400, 50, "Tee uusi "+tool.ContentDescription+" peliin");
        PushButton addContentButton = new PushButton(400, 50, "Lisää tekemäsi sisältö peliin");

        playGameButton.Clicked += PlayGamePressed;
        createContentButton.Clicked += CreateContentPressed;
        addContentButton.Clicked += AddContentPressed;

        playGameButton.Y = Screen.Height / 6;
        Add(playGameButton);
        createContentButton.Y = 0;
        Add(createContentButton);
        addContentButton.Y = -Screen.Height / 6;
        Add(addContentButton);

        taskQueue.Enqueue(new Tuple<GameRecord, Task>(workshopGame, Task.Checkout));
        StartThreadedTaskListProcessor();
    }
    void StopThread()
    {
         processingThread.Abort();
    }
    void PlayGamePressed()
    {
        taskQueue.Enqueue(new Tuple<GameRecord, Task>(workshopGame, Task.UpdateListed));
        taskQueue.Enqueue(new Tuple<GameRecord, Task>(workshopGame, Task.Compile));
        taskQueue.Enqueue(new Tuple<GameRecord, Task>(workshopGame, Task.RunGame));
    }
    void CreateContentPressed()
    {
        InputWindow askFileNameWindow = new InputWindow("Anna luotavalle "+tool.ContentDescription+"tiedostolle nimi");
        askFileNameWindow.TextEntered += ContentNameGiven;
        Add(askFileNameWindow);
    }
    void ContentNameGiven(InputWindow inputWindow)
    {
        string contentBaseFolder = Path.Combine(Directory.GetCurrentDirectory(), workshopGame.PupilGroupName, workshopGame.ContentFolder);
        string templateBaseFolder = Path.Combine(Directory.GetCurrentDirectory(), workshopGame.PupilGroupName, workshopGame.TemplateFolder);

        string templateFilePath = Path.Combine(templateBaseFolder, tool.TemplateFile);
        string contentFileName = Path.GetFileNameWithoutExtension( inputWindow.InputBox.Text )+tool.ContentExt;
        string contentFilePath = Path.Combine(contentBaseFolder, CONTENT_SUBFOLDER, contentFileName);

        if (File.Exists(contentFilePath))
        {
            MessageDisplay.Add( "Tämä nimi on jo varattu. Paina nappia uudelleen ja keksi toinen nimi." );
        }
        else
        {
            if (tool.TemplateFile != "")
            {
                File.Copy(templateFilePath, contentFilePath);
                fileToEdit = contentFilePath;
            }
            else
            {
                fileToEdit = "";
            }
            taskQueue.Enqueue(new Tuple<GameRecord, Task>(workshopGame, Task.RunTool));
        }
    }
    void AddContentPressed()
    {
        taskQueue.Enqueue(new Tuple<GameRecord, Task>(workshopGame, Task.CheckForModified));
    }
    private void ReadGameInfoAndSettings()
    {
        IniFile settingsFile = new IniFile("settings.ini");
        SVN_CLI_EXE = settingsFile.GetSetting("settings", "svn_cli_exe");
        SVN_OPTIONS = settingsFile.GetSetting("settings", "svn_options");
        SVN_PASSWD = settingsFile.GetSetting("settings", "svn_passwd");
        MSBUILD_EXE = settingsFile.GetSetting("settings", "msbuild_exe");
        CONTENT_SUBFOLDER = settingsFile.GetSetting("tools", "content_subfolder");

		tool = new ContentTool(){
            ToolExe=settingsFile.GetSetting("tools", "add_content_exe" ),
            TemplateFile=settingsFile.GetSetting("tools", "content_template_file" ),
            ContentDescription = settingsFile.GetSetting("tools", "content_description"),
            ContentExt = settingsFile.GetSetting("tools", "content_ext")
        };

        // Game
        workshopGame = new GameRecord(){
            PupilGroupName=settingsFile.GetSetting("gameinfo","pupil_group_name"),
            GameName=settingsFile.GetSetting("gameinfo","game_name"),
            SVNRepo=settingsFile.GetSetting("gameinfo","repository_url"),
            toProcess=new List<string>(){"."}, // Root folder
            Solution=settingsFile.GetSetting("gameinfo","solution_file"),
            ContentFolder=settingsFile.GetSetting("gameinfo","content_folder"),
            TemplateFolder=settingsFile.GetSetting("gameinfo","template_folder")
        };
    }

    #region KeypressHandlers
    void PauseProcess()
    {
        paused = !paused;
    }

    void ToggleTopmost()
    {
        topmost = !topmost;
        SetWindowTopmost(topmost);
    }
    #endregion

    void SetWindowTopmost(bool topmost)
    {
        int screenHt = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height;
        int screenWt = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width;
        if (topmost)
        {
            User32.SetWindowPos((uint)this.Window.Handle, HWND_TOPMOST, 0, 0, screenWt, screenHt, 0);
        }
        else
        {
            User32.SetWindowPos((uint)this.Window.Handle, HWND_TOP, 0, 0, screenWt, screenHt, 0);
        }
        this.topmost = topmost;
    }

    /// <summary>
    /// Update processes asynchronous messaging and state changes from the worker (processing) thread.
    /// </summary>
    /// <param name="time"></param>
    protected override void Update(Time time)
    {
        base.Update(time);
        var hasOne = stateQueueMutex.WaitOne(10);
        if (hasOne)
        {
            while (messageQueue.Count > 0)
            {
                string message = messageQueue.Dequeue();
                MessageDisplay.Add(message);
            }

            while (stateQueue.Count > 0)
            {
                var stateChange = stateQueue.Dequeue();
                var gameName = stateChange.Item1.GameName;

                if (stateChange.Item2==Task.RunGame || stateChange.Item2==Task.RunTool)
                {
                    if (topmost && stateChange.Item3==Status.Wait)
                    {
                        SetWindowTopmost(false);
                        wasTopmost = true;
                    }
                    else if (wasTopmost == true && (stateChange.Item3 == Status.OK || stateChange.Item3 == Status.Fail))
                    {
                        SetWindowTopmost(true);
                    }
                }
            }
            stateQueueMutex.ReleaseMutex();
        }
    }

    #region TaskProcessing
    private void StartThreadedTaskListProcessor()
    {
        processing = false;
        processingThread = new Thread(new ThreadStart(ProcessTaskList));

        // Start the thread
        processingThread.Start();

        // Spin for a while waiting for the started thread to become
        // alive:
        while (!processingThread.IsAlive) ;
    }

    void ProcessTaskList()
    {
        while (true)
        {
            if (processing || paused || taskQueue.Count == 0)
            {
                System.Threading.Thread.Sleep((int)(PROCESS_CHECK_INTERVAL));
                continue;
            }

            var task = taskQueue.Dequeue();
            switch (task.Item2)
            {
                case Task.Checkout:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Alustetaan pelin " + task.Item1.GameName + " kansiot");
                    stateQueueMutex.ReleaseMutex();
                    ProcessCheckoutRepo(task.Item1);
                    break;
                case Task.UpdateListed:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Haetaan pelin " + task.Item1.GameName + " tiedostoja");
                    stateQueueMutex.ReleaseMutex();
                    ProcessUpdateListed(task.Item1);
                    break;
                case Task.Compile:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Käännetään peliä " + task.Item1.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessCompile(task.Item1);
                    break;
                case Task.RunGame:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Ajetaan peliä " + task.Item1.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessRunGame(task.Item1);
                    break;
                case Task.RunTool:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Käynnistetään työkalu " + task.Item1.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessRunTool(task.Item1);
                    break;
                case Task.CheckForModified:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Tarkista muuttuneet peliin " + task.Item1.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessCheckModified(task.Item1);
                    break;
                case Task.ChooseFromModified:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Valitse muuttuneet peliin " + task.Item1.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessChooseModified(task.Item1);
                    break;
                case Task.AddListed:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Lisätään sisältö peliin " + task.Item1.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessAddListed(task.Item1);
                    break;
                case Task.CommitListed:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Lähetetään sisältö peliin " + task.Item1.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessCommitListed(task.Item1);
                    break;
                default:
                    break;
            }
            processing = false;
        }
    }

    void ProcessCheckoutRepo(GameRecord record)
    {
        // Directory existance implies existing checkout
        if (Directory.Exists(record.PupilGroupName))
        {
            taskQueue.Enqueue(new Tuple<GameRecord, Task>(record, Task.UpdateListed));
        }
        else
        {
            Directory.CreateDirectory(record.PupilGroupName);
            Task currentTask = Task.Checkout;
            Task nextTask = Task.UpdateListed;
            string command = String.Format("\"{0}\" {1} co {2} \"{3}\"", SVN_CLI_EXE, SVN_OPTIONS, record.SVNRepo, Path.Combine(Directory.GetCurrentDirectory(), record.PupilGroupName));

            GenericProcessor(record, currentTask, nextTask, command);
        }
    }

    /// <summary>
    /// Update the files/folders in the record from svn. A new batch of tasks to update each is added to the queue.
    /// Fail: Does not update for some reason.
    /// OK: Updated w/o problems.
    /// After: Task.Compile
    /// </summary>
    void ProcessUpdateListed(GameRecord record)
    {
        Task currentTask = Task.UpdateListed;
            
        foreach (var toUpdate in record.toProcess)
        {
            Task nextTask = Task.None;
            bool addRetry = false;
            string command = String.Format("\"{0}\" {1} up \"{2}\"", SVN_CLI_EXE, SVN_OPTIONS, Path.Combine(Directory.GetCurrentDirectory(), record.PupilGroupName, toUpdate));
            GenericProcessor(record, currentTask, nextTask, command, -1, addRetry);
        }
    }

    /// <summary>
    /// Compile the .sln with msbuild.
    /// Fail: Does not compile for some reason.
    /// OK: Game compiles without error.
    /// After: Task.UpdateListed
    /// </summary>
    void ProcessCompile(GameRecord record)
    {
        Task currentTask = Task.Compile;
        Task nextTask = Task.None;
        string command = String.Format("\"{0}\" /nologo /noconlog \"{1}\"", MSBUILD_EXE, Path.Combine(Directory.GetCurrentDirectory(), record.PupilGroupName, record.Solution));
        GenericProcessor(record, currentTask, nextTask, command);
    }

    /// <summary>
    /// Run game for the duration of MAX_GAME_RUN_TIME and of no problems arise, kill it. 
    /// Fail: game does not start or crashes
    /// OK: Game runs fone for MAX_GAME_RUN_TIME 
    /// After: Task.UpdateListed
    /// </summary>
    void ProcessRunGame(GameRecord record)
    {
        string gameExeName = "";
        foreach (string file in Directory.EnumerateFiles(
            record.PupilGroupName, "*.exe", SearchOption.AllDirectories))
        {
            if (gameExeName == "")
            {
                gameExeName = file;
            }
            else
            {
                stateQueueMutex.WaitOne();
                messageQueue.Enqueue("Multiple game exes for the game, using " + gameExeName);
                stateQueueMutex.ReleaseMutex();
                break;
            }
        }
        if (gameExeName == "")
        {
            // No game to run. Skip to update.
            taskQueue.Enqueue(new Tuple<GameRecord, Task>(record, Task.UpdateListed));
        }
        else
        {
            Task currentTask = Task.RunGame;
            Task nextTask = Task.None;
            string command = String.Format("\"{0}\"", gameExeName);
            GenericProcessor(record, currentTask, nextTask, command);
        }
    }

    void ProcessRunTool(GameRecord record)
    {
        Task currentTask = Task.RunTool;
        Task nextTask = Task.None;
        string command = String.Format("\"{0}\" \"{1}\"", tool.ToolExe, fileToEdit);
        GenericProcessor(record, currentTask, nextTask, command);
    }

    void ProcessCheckModified(GameRecord record)
    {
        Task currentTask = Task.CheckForModified;
        Task nextTask = Task.ChooseFromModified;
        bool addRetry = false;
        string command = String.Format("\"{0}\" {1} status \"{2}\"", SVN_CLI_EXE, SVN_OPTIONS, Path.Combine(Directory.GetCurrentDirectory(), record.PupilGroupName));
        GenericProcessor(record, currentTask, nextTask, command, -1, addRetry);
    }


    void ProcessChooseModified(GameRecord record)
    {
        List<string> shortSelectionLabels = new List<string>();
        string basePath = Path.Combine(Directory.GetCurrentDirectory(), record.PupilGroupName, record.ContentFolder);
        foreach (var fn in newAndModifiedFileNames)
	    {
            int nameStartPos = fn.IndexOf(record.ContentFolder) + record.ContentFolder.Length;
            string shortfn = fn.Substring(nameStartPos);
            shortSelectionLabels.Add(shortfn);
	    }
        shortSelectionLabels.Add("Peruuta");

        MultiSelectWindow selectToSendWindow = new MultiSelectWindow("Valitse peliin lisättävä tiedosto", shortSelectionLabels.ToArray());
        selectToSendWindow.BorderColor = Color.DarkGray;
        selectToSendWindow.ItemSelected += ModifiedFileSelected;
        selectToSendWindow.DefaultCancel = shortSelectionLabels.Count - 1;
        Add(selectToSendWindow);
    }

    void ModifiedFileSelected(int selection)
    {
        GameRecord addCommitFilesRecord = workshopGame.Clone() as GameRecord;
        addCommitFilesRecord.toProcess = new List<string>();

        //Path.Combine(Directory.GetCurrentDirectory(), workshopGame.PupilGroupName, workshopGame.ContentFolder);

        if (selection>=0 && selection<newAndModifiedFileNames.Count)
        {
            string fileToSend = newAndModifiedFileNames[selection];
            addCommitFilesRecord.toProcess.Add(fileToSend);

            char fileToSendState = newAndModifiedFileStates[selection];
            if (fileToSendState=='?')
            {
                taskQueue.Enqueue(new Tuple<GameRecord, Task>(addCommitFilesRecord, Task.AddListed));
                taskQueue.Enqueue(new Tuple<GameRecord, Task>(addCommitFilesRecord, Task.CommitListed));
            }
            else if (fileToSendState=='M' || fileToSendState=='A')
            {
                taskQueue.Enqueue(new Tuple<GameRecord, Task>(addCommitFilesRecord, Task.CommitListed));
            }
        }
    }

    void ProcessAddListed(GameRecord record)
    {
        Task currentTask = Task.AddListed;
        foreach (var toAdd in record.toProcess)
        {
            Task nextTask = Task.None;
            bool addRetry = false;
            string command = String.Format("\"{0}\" {1} add \"{2}\"", SVN_CLI_EXE, SVN_OPTIONS, toAdd);
            GenericProcessor(record, currentTask, nextTask, command, -1, addRetry);
        }
    }

    void ProcessCommitListed(GameRecord record)
    {
        Task currentTask = Task.CommitListed;
        foreach (var toCommit in record.toProcess)
        {
            string userName = Environment.UserName; 
            Task nextTask = Task.None;
            bool addRetry = false;
            string command = String.Format("\"{0}\" {1} commit \"{2}\" -m \"Automated commit by {3}\"", SVN_CLI_EXE, SVN_OPTIONS, toCommit, userName);
            GenericProcessor(record, currentTask, nextTask, command, 3000, addRetry);
        }
    }
    

    private Status GenericProcessor(GameRecord record, Task currentTask, Task nextTask, string command, int runTimeout = -1, bool addRetry = true)
    {
        Status returnStatus = Status.NA;
        
        if (activeCliProcess == null)
        {
            stateQueueMutex.WaitOne();
            stateQueue.Enqueue(new Tuple<GameRecord, Task, Status>(record, currentTask, Status.Wait));
            stateQueueMutex.ReleaseMutex();

            // split
            string exepart = command.Substring(0, command.IndexOf(".exe\"") + 5);
            string argpart = command.Substring(command.IndexOf(".exe\"") + 5);

            activeCliProcess = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            startInfo.FileName = exepart;
            startInfo.Arguments = argpart;

            /*
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/C "+command;
            */

            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;

            activeCliProcess.StartInfo = startInfo;
            activeCliProcess.Start();

            //messageQueueMutex.WaitOne();
            //messageQueue.Enqueue("Running command " + command);
            //messageQueueMutex.ReleaseMutex();

            if (runTimeout == -1)
            {
                activeCliProcess.WaitForExit();
            }
            else
            {
                activeCliProcess.WaitForExit(runTimeout);
            }
            // The --password option for svn.exe does not work, therefore send it using stdin
            if (currentTask == Task.CommitListed)
            {
                if (SVN_PASSWD!=null)
                    activeCliProcess.StandardInput.WriteLine( SVN_PASSWD );
                activeCliProcess.WaitForExit(runTimeout);
            }
        }
        if (activeCliProcess.HasExited)
        {
            // THIS is probably CMD.exe exitcode
            if (activeCliProcess.ExitCode == 0)
            {
                stateQueueMutex.WaitOne();
                messageQueue.Enqueue("Process exited with CODE 0. Output:");

                if (currentTask == Task.CheckForModified)
                {
                    newAndModifiedFileNames = new List<string>();
                    newAndModifiedFileStates = new List<char>();
                }

                StreamReader sr = activeCliProcess.StandardOutput;
                while (!sr.EndOfStream)
                {
                    String s = sr.ReadLine();
                    if (s != "")
                    {
                        if (currentTask == Task.CheckForModified)
                        {
                            string[] parts = s.Split();
                            newAndModifiedFileNames.Add(s.Substring(s.IndexOf("C:\\")));
                            newAndModifiedFileStates.Add(parts.First()[0]);
                        }

                        Trace.WriteLine(s);
                        //messageQueue.Enqueue(s);
                    }
                }

                stateQueue.Enqueue(new Tuple<GameRecord, Task, Status>(record, currentTask, Status.OK));
                returnStatus = Status.OK;

                stateQueueMutex.ReleaseMutex();


                // TODO: Check if it was successfull, //  update light bulb state and 
                if (nextTask != Task.None)
                    taskQueue.Enqueue(new Tuple<GameRecord, Task>(record, nextTask));
            }
            else
            {
                stateQueueMutex.WaitOne();
                messageQueue.Enqueue("Process exited with CODE 1. Output:");


                StreamReader sro = activeCliProcess.StandardOutput;
                while (!sro.EndOfStream)
                {
                    String s = sro.ReadLine();
                    if (s != "")
                    {
                        Trace.WriteLine(s);
                        //messageQueue.Enqueue(s);
                    }
                }
                StreamReader sre = activeCliProcess.StandardError;
                while (!sre.EndOfStream)
                {
                    String s = sre.ReadLine();
                    if (s != "")
                    {
                        Trace.WriteLine(s);
                        //messageQueue.Enqueue(s);
                    }
                }

                stateQueue.Enqueue(new Tuple<GameRecord, Task, Status>(record, currentTask, Status.Fail));
                returnStatus = Status.Fail;

                stateQueueMutex.ReleaseMutex();

                if (addRetry)
                    taskQueue.Enqueue(new Tuple<GameRecord, Task>(record, currentTask));
            }
        }
        else
        {
            activeCliProcess.Kill();
            stateQueueMutex.WaitOne();
            messageQueue.Enqueue("Process killed");
            stateQueue.Enqueue(new Tuple<GameRecord, Task, Status>(record, currentTask, Status.OK));
            returnStatus = Status.OK;
            stateQueueMutex.ReleaseMutex();

            // TODO: Check if it was successfull, //  update light bulb state and 
            if (nextTask != Task.None)
                taskQueue.Enqueue(new Tuple<GameRecord, Task>(record, nextTask));
        }
        activeCliProcess = null;

        return returnStatus;
    }
    #endregion
}