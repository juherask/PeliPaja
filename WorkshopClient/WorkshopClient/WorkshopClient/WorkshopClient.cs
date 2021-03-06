﻿using System;
using System.Collections.Generic;
using Jypeli;
using Jypeli.Controls;
using Jypeli.Widgets;
using Microsoft.Xna.Framework.Graphics;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Text;

using System.Runtime.InteropServices;
class User32
{
    public const int SW_MAXIMIZE = 3;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public const int HWND_TOP = 0;
    public const int HWND_BOTTOM = 1;
    public const int HWND_TOPMOST = -1;

    [DllImport("user32.dll")]
    public static extern void SetWindowPos(uint hWnd, int Level, int X, int Y, int W, int H, uint Flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(uint hWnd, uint nCmdShow);
}


/* TODO:
 * - Test if the tortoisesvn client accepts password and user as cmd line parameters
 */

/*
* For checking out we use
* > svn checkout <repo> <author_folder> --depth empty
* > cd <author_folder>/trunk
* > svn up <files/folders_you_want>
*/
public class WorkshopClient : Game
{
    static int PROCESS_CHECK_INTERVAL = 100;
    static int PROCESS_CHECK_INTERVAL_BACKGROUND = 3000;
    
    /* Settings etc. */
    int NumberOfToolsInIni = 1;
    ExternalExecutableSettings SettingsFromIni = new ExternalExecutableSettings();
    GameRecord WorkshopGame;
    List<ContentTool> tools = new List<ContentTool>();

    /* State */
    Queue<Tuple<GameRecord, Task>> taskQueue; 
    Process activeCliProcess;
    string fileToEdit;
    int checkInterval = PROCESS_CHECK_INTERVAL;
    bool processing = false;
    bool busy = false;
    bool topmost = true;
    bool loopaction = false;
    bool showDebug = false;
    bool wasTopmost = false;

    // Multithreading support
    Mutex stateQueueMutex = new Mutex();
    Queue<string> messageQueue = new Queue<string>();
    Queue<Tuple<GameRecord, Task, Status>> stateQueue = new Queue<Tuple<GameRecord, Task, Status>>();
    Thread processingThread;

    // Quickhack to keep track of changed files.
    List<string> newAndModifiedFileNames = new List<string>();
    List<char> newAndModifiedFileStates = new List<char>();

    // Quickhack to pass on clicked tool to processor thread
    ContentTool currentTool = null;

    /* UI */
    PushButton playGameButton;
    List<PushButton> createContentButtons = new List<PushButton>();
    PushButton modifyCodeButton;
    PushButton addContentButton;

    Dictionary<Task, string> taskToLabel = new Dictionary<Task, string>()
    {
        {Task.Checkout, "Nouto"},
        {Task.UpdateListed, "Update"},
        {Task.Compile, "Kääntäminen"},
        {Task.RunGame, "Pelin ajo"},
    };

    public override void Begin()
    {
#if DEBUG
        SetWindowSize(1024, 768);
        showDebug = true;
        IsFullScreen = false;
#else
        SetWindowTopmost(topmost);
#endif
        Level.Background.Color = Color.White;

        IsMouseVisible = true;

        // Escape disabled for the "kiosk"-mode. Use Alt+F4 to quit.
        //Keyboard.Listen(Key.Escape, Jypeli.ButtonState.Pressed, ConfirmExit, "Lopeta peli");
        Keyboard.Listen(Key.P, Jypeli.ButtonState.Pressed, PauseProcess, "Pistä tauolle");
        Keyboard.Listen(Key.T, Jypeli.ButtonState.Pressed, ToggleTopmost, "Tuo päällimmäiseksi");
        Keyboard.Listen(Key.L, Jypeli.ButtonState.Pressed, LoopAction, "Luuppaa, eli toista toimintoa");
        Keyboard.Listen(Key.D, Jypeli.ButtonState.Pressed, ShowDebug, "Näytä Debug -viestit");
        Exiting += StopThread;

        /* Read settings from ini-file */
        ReadGameInfoAndSettings();
        if (!CheckExecutablePaths())
            return;

        taskQueue = new Queue<Tuple<GameRecord, Task>>();

        /* Set up the UI */
        GameObject logo = new GameObject(LoadImage("logo"));
        Add(logo);

        // Create buttons
        int totalBtnCnt = 1 + tools.Count + 7;
        int iBtn = 3;
        double step = Screen.Height / totalBtnCnt+2;

        logo.Y = step * (totalBtnCnt / 2 - 2);
        
        playGameButton = new PushButton(400, 50, "Pelaa uusinta versiota pelistä");
        playGameButton.Y = step * (totalBtnCnt / 2 - ++iBtn);
        playGameButton.Clicked += PlayGamePressed;
        Add(playGameButton);

        foreach (var tool in tools)
        {
            ContentTool loopToolRef = tool;
            PushButton createContentButton = new PushButton(400, 50, "Tee uusi " + tool.ContentDescription + " peliin");
            createContentButton.Clicked += () => CreateContentPressed(loopToolRef);
            createContentButton.Y = step * (totalBtnCnt / 2 - ++iBtn);
            Add(createContentButton);
            createContentButtons.Add(createContentButton);
        }

        // Optional
        if (SettingsFromIni.VCSExePath!="" && File.Exists(SettingsFromIni.VCSExePath))
        {
            modifyCodeButton = new PushButton(400, 50, "Muuta pelin ohjelmakoodia");
            modifyCodeButton.Y = step * (totalBtnCnt / 2 - ++iBtn);
            modifyCodeButton.Clicked += ModifyCodePressed;
            Add(modifyCodeButton);
        }

        addContentButton = new PushButton(400, 50, "Lisää tekemäsi sisältö peliin");
        addContentButton.Y = step * (totalBtnCnt / 2 - ++iBtn);
        addContentButton.Clicked += AddContentPressed;
        Add(addContentButton);

        taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.Checkout));
        StartThreadedTaskListProcessor();

        EnableButtons(true);
    }
    void StopThread()
    {
        if (processingThread!=null)
            processingThread.Abort();
    }
    void PlayGamePressed()
    {
        if (busy) return; // Do not enque tasks on paused
        taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.UpdateListed));
        taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.Compile));
        taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.RunGame));
    }
    void CreateContentPressed(ContentTool tool)
    {
        if (busy) return; // Do not enque tasks on paused
        currentTool = tool;
        StartTool();
    }
    void ModifyCodePressed()
    {
        if (busy) return; // Do not enque tasks on paused
        currentTool = new ContentTool(){
            ToolExe = SettingsFromIni.VCSExePath,
            FileToEditOverride = Path.Combine(Directory.GetCurrentDirectory(), WorkshopGame.PupilGroupName, WorkshopGame.Solution)
            };
        StartTool();
    }
    void AddContentPressed()
    {
        if (busy) return; // Do not enque tasks on paused
        taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.CheckForModified));
    }

    void ContentNameGiven(InputWindow inputWindow)
    {
        string namedName = inputWindow.InputBox.Text;

        string newContentFilePath = "";
        string namedContentFilePath = "";
        GetContentFilePaths("uusi_" + currentTool.ContentSubfolder, inputWindow.InputBox.Text, ref newContentFilePath, ref namedContentFilePath);

        if (inputWindow.InputBox.Text != "")
        {    
            if (File.Exists(namedContentFilePath))
            {
                MessageDisplay.Add("Tämä nimi on jo varattu. Paina nappia uudelleen ja keksi toinen nimi.");
                NameContent();
            }
            else if (!File.Exists(newContentFilePath))
            {
                MessageDisplay.Add("Fataali virhe, uutta luotua sisältötiedostoa ei löydy");
            }
            else
            {
                System.IO.File.Move(newContentFilePath, namedContentFilePath);
            }
        }
        else
        {
            MultiSelectWindow valintaIkkuna = new MultiSelectWindow("Ilman nimeä tiedosto poistetaan. Oletko varma?", "Kyllä", "Ei");
            valintaIkkuna.ItemSelected += new Action<int>(selection => NoNameConfirmation(selection, newContentFilePath));
            Add(valintaIkkuna);
        }
    }
    void NoNameConfirmation(int selection, string namelessFilePath)
    {
        switch (selection)
        {
            case 0:
                if (File.Exists(namelessFilePath))
                    File.Delete(namelessFilePath);
                break;
            case 1:
                NameContent();
                break;
            default:
                break;
        }
    }

    void GetContentFilePaths(string contentNameToCreate, string contentNameToRenameTo, ref string newContentFilePath, ref string namedContentFilePath)
    {
        string newContentFileName = Path.GetFileNameWithoutExtension(contentNameToCreate) + currentTool.ContentExt;
        string namedContentFileName = Path.GetFileNameWithoutExtension(contentNameToRenameTo) + currentTool.ContentExt;

        string contentBaseFolder = Path.Combine(Directory.GetCurrentDirectory(), WorkshopGame.PupilGroupName, WorkshopGame.ContentFolder);
        newContentFilePath = Path.Combine(contentBaseFolder, currentTool.ContentSubfolder, newContentFileName);
        namedContentFilePath = Path.Combine(contentBaseFolder, currentTool.ContentSubfolder, namedContentFileName);
    }

    private void StartTool()
    {
        /* Check if template file creation is needed and do it */
        string templateFilePath = GetTemplateFilePath();
        string newContentFilePath ="";
        string namedContentFilePath ="";
        GetContentFilePaths("uusi_" + currentTool.ContentSubfolder, "", ref newContentFilePath, ref namedContentFilePath);
        if (currentTool.TemplateFile != "")
        {
            if (File.Exists(newContentFilePath))
                File.Delete(newContentFilePath);
            File.Copy(templateFilePath, newContentFilePath);
            fileToEdit = newContentFilePath;
        }
        else if (currentTool.FileToEditOverride != "")
        {
            fileToEdit = currentTool.FileToEditOverride;
        }
        else {
            fileToEdit = "";
        }

        /* Start the tool */
        taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.RunTool));
    }

    private string GetTemplateFilePath()
    {
        string templateBaseFolder = Path.Combine(Directory.GetCurrentDirectory(), WorkshopGame.PupilGroupName, WorkshopGame.TemplateFolder);
        string templateFilePath = Path.Combine(templateBaseFolder, currentTool.TemplateFile);
        return templateFilePath;
    }

    // Crude byte to byte comparer
    static bool FileEquals(string path1, string path2)
    {
        byte[] file1 = File.ReadAllBytes(path1);
        byte[] file2 = File.ReadAllBytes(path2);
        if (file1.Length == file2.Length)
        {
            for (int i = 0; i < file1.Length; i++)
            {
                if (file1[i] != file2[i])
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    void NameContent()
    {
        if (currentTool.TemplateFile != "")
        {
            string templateFilePath = GetTemplateFilePath();
            string newContentFilePath = "";
            string namedContentFilePath = "";
            GetContentFilePaths("uusi_" + currentTool.ContentSubfolder, "", ref newContentFilePath, ref namedContentFilePath);

            // Check that file has been changed (from the template)
            if (File.Exists(templateFilePath) &&
                File.Exists(newContentFilePath) &&
                !FileEquals(templateFilePath, newContentFilePath))
            {
                InputWindow askFileNameWindow = new InputWindow("TÄRKEÄÄ: Anna luodulle " + currentTool.ContentDescription + "-tiedostolle kuvaava nimi");
                askFileNameWindow.TextEntered += ContentNameGiven;
                Add(askFileNameWindow);
            }
            else if (File.Exists(newContentFilePath))
            {
                File.Delete(newContentFilePath);
            }
        }
        else
        {
            // No template -> named in the tool
        }
    }


    

    private void ReadGameInfoAndSettings()
    {
        IniFile settingsFile = new IniFile("settings.ini");
        SettingsFromIni.SvnCliExePath = settingsFile.GetSetting("settings", "svn_cli_exe");
        SettingsFromIni.SvnOptions = settingsFile.GetSetting("settings", "svn_options");
        SettingsFromIni.SvnInteractivePassword = settingsFile.GetSetting("settings", "svn_passwd");
        SettingsFromIni.MsbuildExePath = settingsFile.GetSetting("settings", "msbuild_exe");
        SettingsFromIni.VCSExePath = settingsFile.GetSetting("settings", "vcs_exe");

        NumberOfToolsInIni = Int32.Parse(settingsFile.GetSetting("tools", "number_of_tools"));

        // 1 based indexing!
        for (int i = 1; i <= NumberOfToolsInIni; i++)
        {
            string prefix = String.Format("tool{0}_", i);
            tools.Add(new ContentTool()
            {
                ToolExe = settingsFile.GetSetting("tools", prefix + "add_content_exe"),
                TemplateFile = settingsFile.GetSetting("tools", prefix + "content_template_file"),
                ContentDescription = settingsFile.GetSetting("tools", prefix + "content_description"),
                ContentSubfolder = settingsFile.GetSetting("tools", prefix + "content_subfolder"),
                ContentExt = settingsFile.GetSetting("tools", prefix + "content_ext")
            });
        }

        // Game
        WorkshopGame = new GameRecord(){
            PupilGroupName=settingsFile.GetSetting("gameinfo","pupil_group_name"),
            GameName=settingsFile.GetSetting("gameinfo","game_name"),
            SVNRepo=settingsFile.GetSetting("gameinfo","repository_url"),
            toProcess=new List<string>(){"."}, // Root folder
            Solution=settingsFile.GetSetting("gameinfo","solution_file"),
            ContentFolder=settingsFile.GetSetting("gameinfo","content_folder"),
            TemplateFolder=settingsFile.GetSetting("gameinfo","template_folder")
        };
    }

    bool CheckExecutablePaths()
    {
        StringBuilder errorsBuilder = new StringBuilder("Virheitä annetuissa poluissa: ");
        bool isErrors = false;

        /* Required */
        if (!File.Exists(SettingsFromIni.SvnCliExePath))
        {
            errorsBuilder.Append("Vaadittua ohjelmaa svn.exe ei löydy annetusta polusta. ");
            isErrors = true;
        }
        if (!File.Exists(SettingsFromIni.MsbuildExePath))
        {
            errorsBuilder.Append("Vaadittua ohjelmaa msbuild.exe ei löydy annetusta polusta. ");
            isErrors = true;
        }

        /* Optional */
        //if (!File.Exists(SettingsFromIni.VCSExePath)) { }

        /* Tools */
        foreach (var tool in tools)
        {
            if (!File.Exists(tool.ToolExe))
            {
                errorsBuilder.Append(String.Format("Työkalua {0} ei löydy annetusta polusta. ", Path.GetFileName(tool.ToolExe)));
                isErrors = true;
            }
        }

        if (isErrors)
        {
            string errors = errorsBuilder.ToString();
            MessageWindow message = new MessageWindow(errors);
            message.Closed+=(sender => Exit());
            Add(message, 3);
            return false;
        }
        return true;
    }

    #region KeypressHandlers
    void PauseProcess()
    {
        busy = !busy;
        if (busy)
            MessageDisplay.Add("Pausella.");
        else
            MessageDisplay.Add("Jatkaa.");
    }

    void ToggleTopmost()
    {
        topmost = !topmost;
        SetWindowTopmost(topmost);
        SetWindowMinimized(false);
    }

    void LoopAction()
    {
        loopaction = !loopaction;
        if (loopaction)
            MessageDisplay.Add("Pelin käynnistämisen toisto PÄÄLLÄ.");
        else
        {
            MessageDisplay.Add("Pelin käynnistämisen toisto POIS.");

            while (taskQueue.Count > 0)
            {
                taskQueue.Dequeue();
            }
        }
    }

    void ShowDebug()
    {
        showDebug = !showDebug;
        if (showDebug)
            MessageDisplay.Add("Näyttää debugviestit.");
        else
            MessageDisplay.Add("Piilottaa debugviestit.");
    }
    #endregion

    void SetWindowTopmost(bool topmost)
    {
        IsFullScreen = topmost;
    }
    void SetWindowMinimized(bool minimize)
    {
        if (minimize)
            User32.ShowWindow((uint)this.Window.Handle, User32.SW_MINIMIZE);
        else
            User32.ShowWindow((uint)this.Window.Handle, User32.SW_RESTORE);
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
                if (showDebug)
                    MessageDisplay.Add(message);
            }

            while (stateQueue.Count > 0)
            {
                var stateChange = stateQueue.Dequeue();
                var gameName = stateChange.Item1.GameName;

                if (stateChange.Item3 == Status.Wait)
                {
                    EnableButtons(false);
                }
                else
                {
                    EnableButtons(true);
                }

                if (stateChange.Item2==Task.RunGame || stateChange.Item2==Task.RunTool)
                {
                    if (topmost && stateChange.Item3==Status.Wait)
                    {
                        SetWindowTopmost(false);
                        SetWindowMinimized(true);
                        wasTopmost = true;
                        checkInterval = PROCESS_CHECK_INTERVAL_BACKGROUND;
                        if (!IsPaused)
                            Pause();
                    }
                    else if (wasTopmost == true && (stateChange.Item3 == Status.OK || stateChange.Item3 == Status.Fail))
                    {
                        SetWindowMinimized(false);
                        SetWindowTopmost(true);
                        checkInterval = PROCESS_CHECK_INTERVAL;
                        EnableButtons(true);
                        if (IsPaused)
                            Pause(); // unpause
                    }
                }

                if (stateChange.Item2 == Task.RunTool && stateChange.Item3 == Status.OK)
                {
                    // Tool was run. Check if new created content needs to be renamed
                    NameContent();
                }

                if (stateChange.Item2 == Task.RunGame && stateChange.Item3 == Status.OK && loopaction)
                {
                    // Repeat action.
                    taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.UpdateListed));
                    taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.Compile));
                    taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.RunGame));
                }

                
            }
            stateQueueMutex.ReleaseMutex();
        }
    }
    protected override void PausedUpdate(Time time)
    {
        base.PausedUpdate(time);
        var hasOne = stateQueueMutex.WaitOne(10);
        if (hasOne)
        {
            while (stateQueue.Count > 0)
            {
                var stateChange = stateQueue.Dequeue();
                var gameName = stateChange.Item1.GameName;

                if (stateChange.Item3 != Status.Wait)
                {
                    EnableButtons(true);
                }

                if (stateChange.Item2 == Task.RunGame || stateChange.Item2 == Task.RunTool)
                {
                    if (wasTopmost == true && (stateChange.Item3 == Status.OK || stateChange.Item3 == Status.Fail))
                    {
                        SetWindowMinimized(false);
                        SetWindowTopmost(true);
                        
                        checkInterval = PROCESS_CHECK_INTERVAL;
                        EnableButtons(true);
                        if (IsPaused)
                            Pause(); // unpause
                    }
                }
                if (stateChange.Item2 == Task.RunTool && stateChange.Item3 == Status.OK)
                {
                    // Tool was run. Check if new created content needs to be renamed
                    NameContent();
                }
                if (stateChange.Item2 == Task.RunGame && stateChange.Item3 == Status.OK && loopaction)
                {
                    // Repeat action.
                    taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.UpdateListed));
                    taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.Compile));
                    taskQueue.Enqueue(new Tuple<GameRecord, Task>(WorkshopGame.Clone() as GameRecord, Task.RunGame));
                }
            }
            stateQueueMutex.ReleaseMutex();
        }
    }

    void EnableButtons(bool enable)
    {
        if (enable)
        {
            playGameButton.Color = Color.Darker( Color.LightGreen, 100 );
            foreach (var createContentButton in createContentButtons)
            {
                createContentButton.Color = Color.Darker(Color.Yellow, 100);
            }
            modifyCodeButton.Color = Color.Darker(Color.Blue, 100);
            addContentButton.Color =  Color.Darker( Color.Pink, 100 );
        }
        else
        {
            playGameButton.Color = Color.DarkGray;
            foreach (var createContentButton in createContentButtons)
            {
                createContentButton.Color = Color.DarkGray;
            }
            modifyCodeButton.Color = Color.DarkGray;
            addContentButton.Color = Color.DarkGray;
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
            if (processing || busy || taskQueue.Count == 0)
            {
                busy = false;
                System.Threading.Thread.Sleep(checkInterval);
                continue;
            }

            busy = true;
            var taskTuple = taskQueue.Dequeue();
            Task task = taskTuple.Item2;
            GameRecord record = taskTuple.Item1;
            switch (task)
            {
                case Task.Checkout:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Alustetaan pelin " + record.GameName + " kansiot");
                    stateQueueMutex.ReleaseMutex();
                    ProcessCheckoutRepo(record);
                    break;
                case Task.UpdateListed:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Haetaan pelin " + record.GameName + " tiedostoja");
                    stateQueueMutex.ReleaseMutex();
                    ProcessUpdateListed(record);
                    break;
                case Task.Compile:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Käännetään peliä " + record.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessCompile(record);
                    break;
                case Task.RunGame:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Ajetaan peliä " + record.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessRunGame(record);
                    break;
                case Task.RunTool:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Käynnistetään työkalu " + record.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessRunTool(record);
                    break;
                case Task.CheckForModified:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Tarkista muuttuneet peliin " + record.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessCheckModified(record);
                    break;
                case Task.ChooseFromModified:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Valitse muuttuneet peliin " + record.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessChooseModified(record);
                    break;
                case Task.AddListed:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Lisätään sisältö peliin " + record.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessAddListed(record);
                    break;
                case Task.CommitListed:
                    stateQueueMutex.WaitOne();
                    messageQueue.Enqueue("Lähetetään sisältö peliin " + record.GameName);
                    stateQueueMutex.ReleaseMutex();
                    ProcessCommitListed(record);
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
            string command = String.Format("\"{0}\" {1} co {2} \"{3}\"", SettingsFromIni.SvnCliExePath, SettingsFromIni.SvnOptions, record.SVNRepo, Path.Combine(Directory.GetCurrentDirectory(), record.PupilGroupName));

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
            string command = String.Format("\"{0}\" {1} up \"{2}\"", SettingsFromIni.SvnCliExePath, SettingsFromIni.SvnOptions, Path.Combine(Directory.GetCurrentDirectory(), record.PupilGroupName, toUpdate));
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
        string command = String.Format("\"{0}\" /nologo /noconlog \"{1}\"", SettingsFromIni.MsbuildExePath, Path.Combine(Directory.GetCurrentDirectory(), record.PupilGroupName, record.Solution));
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
            if (file.Contains("bin"))
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
        string command = String.Format("\"{0}\" \"{1}\"", currentTool.ToolExe, fileToEdit);
        GenericProcessor(record, currentTask, nextTask, command);
    }

    void ProcessCheckModified(GameRecord record)
    {
        Task currentTask = Task.CheckForModified;
        Task nextTask = Task.ChooseFromModified;
        bool addRetry = false;
        string command = String.Format("\"{0}\" {1} status \"{2}\"", SettingsFromIni.SvnCliExePath, SettingsFromIni.SvnOptions, Path.Combine(Directory.GetCurrentDirectory(), record.PupilGroupName));
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
        GameRecord addCommitFilesRecord = WorkshopGame.Clone() as GameRecord;
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
            string command = String.Format("\"{0}\" {1} add \"{2}\"", SettingsFromIni.SvnCliExePath, SettingsFromIni.SvnOptions, toAdd);
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
            string command = String.Format("\"{0}\" {1} commit \"{2}\" -m \"Automated commit by {3}\"", SettingsFromIni.SvnCliExePath, SettingsFromIni.SvnOptions, toCommit, userName);
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

            // TODO: Sfxr is quickhack, better to do it in a way that is settable in tools for settings.ini 
            if (currentTask == Task.RunGame || exepart.Contains("sfxr"))
            {
                startInfo.WorkingDirectory = Path.GetDirectoryName(exepart.Replace('"', ' ').Trim());
            }

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
            // TODO: DEBUG. REMOVE!!
            //return Status.OK;

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
                if (SettingsFromIni.SvnInteractivePassword!=null)
                    activeCliProcess.StandardInput.WriteLine(SettingsFromIni.SvnInteractivePassword);
                activeCliProcess.WaitForExit(runTimeout);
            }
        }
        if (activeCliProcess.HasExited)
        {
            // THIS is probably CMD.exe exitcode
            if (activeCliProcess.ExitCode == 0)
            {
                stateQueueMutex.WaitOne();
                messageQueue.Enqueue("Process exited with CODE 0.");

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
                messageQueue.Enqueue("Process exited with CODE 1.");


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