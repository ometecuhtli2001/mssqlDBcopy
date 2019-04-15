using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net.Mail;

namespace mssqlDBcopy
{
    class Program
    {
        /// <summary>Contains results of a RESTORE FILELISTONLY call</summary>
        private struct stFileListEntry
        {
            public string logicalname;
            public string physicalname;
            public string type;
            public string filegroupname;

        } // stFileList

        private static bool keeplog = false;                // Do not keep the log file from previous executions
        private static string logfile = "mssqldbcopy.log";  // Log file for output (regular and debug)

        // Source and destination information
        private static string src_instance;
        private static string src_dbname;
        private static string src_user = "";   // Blank if using current user context (default)
        private static string src_pass = "";
        private static string dest_instance;
        private static string dest_dbname;
        private static string dest_user = "";   // Blank if using current user context (default)
        private static string dest_pass = "";

        private static string mail_recipient="";    // For mailing of log
        private static string mail_server="";

        private static SqlConnection src_con;   // Source instance connection
        private static SqlConnection dest_con;  // Target instance connection

        private static string MDFdir = "";      // Where source MDF file(s) live
        private static string LDFdir = "";      // Where source LDF file(s) live
        private static int backuplog = 0;       // Flag indicating if there's a transaction log for the specified DB
        //private static string logicalname_d = "";   // Logical name of database file
        //private static string logicalname_l = "";   // Logcial name of transaction log file

        /* Separate variables for the holding path because the source and destination instances can refer to the same place 
         * in different ways.  Note that if an instance is on Linux, and the holding area is not local to that box, it must
         * already be mounted before running this utility.
         */
//        private static string holdingpath = "";     // Where to put the backup files during transfer from source -> destination
//        private static string save_to = "";     // How to reference holding area in source instance
//        private static string read_from = "";    // How to reference holding area in destination instance
        private static string save_to = "";             // Location of files for BACKUP - the source SQL instance writes them to this path
        private static string copy_from = "";           // Source path for file copy
        private static string copy_to = "";             // Destination path for file copy
        private static string read_from = "";           // Location of files for RESTORE - the destination SQL instance reads them from this path


        private static bool setPIPESperms = false;
        private static bool dest_overwrite = false;     // Safety first!
        private static bool debug = false;              // Debug mode (defaults to off)
        private static bool cleanup = false;            // Clean up backup files (defaults to no)
        private static bool killswitch = false;         // User specified /KILL ?
        private static bool nop = false;                // If true, go through the motions but don't execute any BACKUP/RESTORE/KILL

        static int Main(string[] args)
        {
            int ret = 0;    // Be optimistic
            bool proceed;   // Okay to proceed to next section

            List<stFileListEntry> files = new List<stFileListEntry>();  // File list of database to be copied

            if (args.Length < 2)
            {
                Console.WriteLine("Please specify a source instance and database followed by a target instance and database. Options come after that.");
                Console.WriteLine("\tmssqldbcopy source-instance:database target-instance:database [/REPLACE|/PIPESPERMS|/CLEANUP|/KILL][/SRC_CREDS:user:pass][/DEST_CREDS:user:pass][[/PATH=holding-path]|[/BACKUP_TO=source-holding-path /RESTORE_FROM=destination-holding-path]][/LOG=log-file]");
                Console.WriteLine();
                Console.WriteLine("Note if you specify /PATH applied to both source and destination, so you cannot specify /BACKUP_TO or /RESTORE_FROM with /PATH.  Likewise, /BACKUP_TO and /RESTORE_FROM go together - you must specify both, and then you cannot use /PATH.");
                Console.WriteLine();
            }
            else
            {
                // Check for debug mode and turn it on - do this as early as possible so command line parsing debug messages show up if needed
                foreach(string s in args)
                {
                    if (s.ToUpper() == "/DEBUG") debug = true;
                    if (s.ToUpper().StartsWith("/LOG="))
                    {
                        logfile = s.Split('=')[1];
                        if (logfile == "") logfile = "mssqlDBcopy.log"; // Revert if the log file pathspec is empty
                    } // if: change log file
                } // foreach

                //proceed = GetEnvVars(); // Get settings from environment variables

                //if(proceed)
                proceed =ParseCommandLine(args); // Now parse command line arguments, which can override environment variables

                // At this point, output all settings
                DebugMessage(string.Format("SRC: user={0} instance={1}  DB={2}", src_user, src_instance, src_dbname));
                DebugMessage(string.Format("DEST: user={0} instance={1}  DB={2}", dest_user, dest_instance, dest_dbname));
                DebugMessage(string.Format("Misc: replace={0} PIPES perms={1} NOP={2}", dest_overwrite.ToString(), setPIPESperms.ToString(), nop.ToString()));
                DebugMessage(string.Format("SAVE_TO={0} COPY_FROM={1}", save_to, copy_from));
                DebugMessage(string.Format("COPY_TO={0} READ_FROM={1}", copy_to, read_from));

                //proceed = false;
                //ret = -1;

                /* Go through settings and check for sanity */
                if (proceed) ret = DoArgumentsMakeSense();
                if (ret != 0)
                {
                    // Eventually, some handling code may go here...
                }
                else
                {
                    if (!keeplog) DeleteOldLog(logfile);
                    if (SqlConnect())
                    {
                        if (SourceDBexists(src_con, src_dbname))    // Does the source DB exist in the source instance?
                        {
                            if (GetDefaultDirs(dest_con))           // Get file system directories in the dest instance
                            {
                                backuplog = HasTLog(src_con, src_dbname);   // Check if DB needs an explicity transaction log backup
                                if (backuplog != -1)
                                {
                                    ret= GetSourceData(src_con, src_dbname, save_to,backuplog,out files); // Get a copy of the source data
                                    if (ret==0)
                                    {
                                        if (copy_from != "") proceed = CopyFiles(copy_from, src_dbname, copy_to);   // If files need copying, do it now
                                        if (proceed)
                                        {
                                            if (killswitch) proceed = Kill(dest_con, dest_dbname);
                                            if (proceed && dest_overwrite) proceed = DropDestDB(dest_con, dest_dbname); // Drop the destination DB to be replaced only once the files are safely within the instance's range for restore operations
                                            if (proceed) ret = PutDestData(dest_con, dest_dbname, read_from, backuplog, src_dbname, files);

                                            if (ret == 0)
                                            {
                                                if (cleanup) DoCleanUp(new string[] { save_to, copy_from, copy_to }, src_dbname, src_con); // If save_to is used, it will point to the same place as read_from

                                                // Extras
                                                if (setPIPESperms) ApplyPIPESperms(dest_con, dest_dbname);
                                            } // if: PutDestData successful?
                                        } // if: proceed with drop destination and restore?
                                    } // if: GetSourceData
                                }
                                else
                                {
                                    ret = 300;
                                } // if..else: HasTLog
                            } // if: GetDefaultDirs
                        } else
                        {
                            ret = 200;
                            Message(string.Format("Database {0} does not exist in instance {1}, or there was an error checking on it. If there were problems, there should be an error message above this one.", src_dbname, src_instance));
                        } // if..else: SourceDBexists
                    } // if: SqlConnect
                } // if..else: passed argument sanity check?
            } // if..else: proper parameter count?

            // Mail the log at this point, if requested
            if((mail_server !="") && (mail_recipient != "")){
			    using (MailMessage mailMessage = new MailMessage(mail_recipient, mail_recipient))
			    {
				    using (SmtpClient smtpClient = new SmtpClient())
				    {
					    smtpClient.Host = mail_server;
					    mailMessage.Subject = "MSSQL DB copy";
					    mailMessage.IsBodyHtml = false;
                        try
                        {
                            mailMessage.Body = System.IO.File.ReadAllText(logfile);
                        } // try
                        catch (Exception ex)
                        {
                            mailMessage.Body = "Error reading log file to e-mail: " + ex.ToString();
                        } // catch
					    smtpClient.Send(mailMessage);
				    } // using: SmtpClient
			    } // using: MailMessage
            } // if: mail log?

            if (Debugger.IsAttached)
            {
                Console.WriteLine("Done!");
                Console.ReadKey();
            } // if: debugging?

            return ret; // Return an errorcode to the caller
        } // Main


        #region Arguments
        /// <summary>Get environment variables</summary>
        /// <returns>FALSE: errors, TRUE: success</returns>
        private static bool GetEnvVars()
        {
            DebugMessage("GetEnvVars:start");
            bool ret = true;
            string act = "";
            try
            {
                act = "MSSQLDBCOPY_PATH";
                if (Environment.GetEnvironmentVariable("MSSQLDBCOPY_PATH") != null)
                {
                    string tmp= Environment.GetEnvironmentVariable("MSSQLDBCOPY_PATH");
                    save_to = tmp;
                    read_from = tmp;
                } // if: MSSQLDBCOPY_PATH
                act = "MSSQLDBCOPY_save_to";
                if (Environment.GetEnvironmentVariable("MSSQLDBCOPY_SAVETO") != null) save_to = Environment.GetEnvironmentVariable("MSSQLDBCOPY_SAVETO");
                act = "MSSQLDBCOPY_read_from";
                if (Environment.GetEnvironmentVariable("MSSQLDBCOPY_READFROM") != null) read_from = Environment.GetEnvironmentVariable("MSSQLDBCOPY_READFROM");
            }
            catch (Exception ex)
            {
                ret = false;
                Message(string.Format("WARNING: There was a problem checking on or retrieving the {0} environment variable: {1}", act, ex.ToString()));
            }

            DebugMessage(string.Format("GetEnvVars={0}", ret.ToString()));

            return ret;
        } // GetEnvVars

        /// <summary>Do the arguments supplied to the utility make sense?</summary>
        /// <returns>0=yes, nonzero=what doesn't make sense</returns>
        private static int DoArgumentsMakeSense()
        {
            int ret = 0;    // Be optimistic!

            // Check holding path settings - specify SAVE_TO and READ_FROM or PATH, but not both
            if (((save_to == "") || (read_from == "")))
            {
                ret = 100;
                Message("ERROR: Please use either /PATH or both /SAVE_TO and /READ_FROM to specify a place for the files to stay during the transfer.");
            } // if: is there a holding path?

            // check COPY_FROM and COPY_TO

            // First, they should not be used if the database is being copied on the same instance - where's the sense in that?
            if(
                    ((copy_from != "") || (copy_to != ""))
                    &&
                    ( src_instance.ToUpper()==dest_instance.ToUpper())
                )
            {
                ret = 100;
                Message("You cannot use /COPY_FROM and /COPY_TO when the source and destination are the same instance!");
            } // if

            // Second, if they're going to be used, they both must be specified.  At the same time, if they're not going to be used, 
            //  neither of them should be specified.
            // The logic here is "If they're both not blank, and they're not both blank, one or the other must have a value, which is bad."
            //  Confusing, huh? Also, don't say anything if there's already been a parameter problem.  Why bother?
            if ( (ret ==0) &&
                !((copy_from=="") && (copy_to==""))
                &&
                !((copy_from != "") && (copy_to != ""))
                )
            {
                ret = 100;
                Message("You must specify both /COPY_FROM and /COPY_TO.");
            } // if: COPY_FROM and COPY_TO check

            // Was the kill switch indicated, but not the replace switch?  This doesn't make sense - why kill connections if there's no database to replace?
            //  The reverse though is okay - you can specify the replace switch without specifying the kill switch.
            if(killswitch && !dest_overwrite)
            {
                Message("You specified the kill switch but did not indicate the destination database should be overwritten.  To ensure safety, this utility will now exit.");
                ret = 100;
            } // if: kill but no replace?

            return ret;
        } // DoArgumentsMakeSense

        /// <summary>Parse command line arguments</summary>
        /// <param name="args">Array of command line arguments</param>
        private static bool ParseCommandLine(string[] args)
        {
            DebugMessage("ParseCommandLine() start\nArguments: " + String.Join("|", args));
            bool ret = true;    // Be optimistic!

            // Source and destination are always the first two arguments
            src_instance = args[0].Split(':')[0];
            src_dbname = args[0].Split(':')[1];
            dest_instance = args[1].Split(':')[0];
            dest_dbname = args[1].Split(':')[1];

            // Now comes optional arguments which use a colon (i.e., /ARG:value)
            foreach (string sw in args)
            {
                switch (sw.Split(':')[0].ToUpper())
                {
                    case "/REPLACE":
                        dest_overwrite = true;
                        break;
                    case "/PIPESPERMS":
                        setPIPESperms = true;
                        break;
                    case "/SRC_CREDS":
                        try
                        {
                            src_user = sw.Split(':')[1];
                            src_pass = sw.Split(':')[2];
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Error parsing source credentials: make sure it is in the format username:password\nExiting as a precaution.\nError: " + ex.ToString());
                            nop = true;
                        }
                        break;
                    case "/DEST_CREDS":
                        try
                        {
                            dest_user = sw.Split(':')[1];
                            dest_pass = sw.Split(':')[2];
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Error parsing destination credentials: make sure it is in the format username:password\nExiting as a precaution.\nError: " + ex.ToString());
                            nop = true;
                        }
                        break;
                    case "/NOP":
                        Message("No operation mode enabled.");
                        nop = true;
                        break;
                    case "/DEBUG":
                        debug = true;
                        Message("Debug mode is now on.");
                        break;
                    case "/CLEANUP":
                        cleanup = true;
                        break;
                    case "/KILL":
                        killswitch = true;
                        DebugMessage("KILL SWITCH SPECIFIED");
                        break;
                    case "/MAILLOG":
                        try
                        {
                            mail_recipient = sw.Split(':')[1];
                            mail_server = sw.Split(':')[2];
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Error parsing log mail settings: make sure it is in the format recipient-address:SMTP-server\nExiting as a precaution.\nError: " + ex.ToString());
                            nop = true;
                        }
                        break;
                } // switch: sw

                // Arguments using an equal sign (i.e., /ARG=value)
                foreach (string arg in new string[] { "/PATH", "/SAVE_TO", "/COPY_FROM", "/COPY_TO", "/READ_FROM" })
                {
                    if (sw.ToUpper().StartsWith(arg))
                    {
                        switch (arg)
                        {
                            case "/PATH":
                                if ((save_to=="") && (read_from == "")){
                                    save_to = sw.Split('=')[1];
                                    read_from = save_to;
                                }
                                else
                                {
                                    ret = false;
                                    Message("/SAVE_TO and/or /READ_FROM already specified - /PATH would override both of them.  Do you want to do this?  As a precaution, exiting.");
                                } // if..else
                                break;
                            case "/SAVE_TO":
                                if (save_to == "")
                                {
                                    save_to = sw.Split('=')[1];
                                }
                                else
                                {
                                    ret = false;
                                    Message("/PATH already specified - do you really want to override it with /SAVE_TO?  Exiting as a precaution.");
                                } // if
                                break;
                            case "/COPY_FROM":
                                copy_from = sw.Split('=')[1];
                                break;
                            case "/COPY_TO":
                                copy_to = sw.Split('=')[1];
                                break;
                            case "/READ_FROM":
                                if (read_from == "")
                                {
                                    read_from = sw.Split('=')[1];
                                }
                                else
                                {
                                    ret = false;
                                    Message("/PATH already specified - do you really want to override it with /READ_FROM?  Exiting as a precaution.");
                                }
                                break;
                            default:
                                Message(string.Format("Unknown option '{0}' : skipping", arg));
                                break;
                        } // switch: which path
                    } // if: holding path specified?
                } // foreach: iterate over arguments which use an = instead of a colon
            } // foreach: iterate over parameters

            DebugMessage("ParseCommandLine = " + ret.ToString());
            return ret;
        } // ParseCommandLine

        #endregion

        #region "Support"

        /// <summary>Forcefully disconnect sessions monopolizing the destination database - WARNING - DANGEROUS!</summary>
        /// <param name="dest"></param>
        /// <param name="destdb"></param>
        /// <returns></returns>
        private static bool Kill(SqlConnection dest, string destdb)
        {
            bool ret = true; // Be optimistic!
            string act = "";

            DebugMessage(string.Format("Kill({0},{1})", dest.DataSource, destdb));

            try
            {
                act = "retrieving kill switch SQL";
                string sql = Properties.Resources.find_to_kill;
                sql = sql.Replace("{DBNAME}", destdb);  // Fill in the name of the destination database

                List<int> spids = new List<int>();

                // Get a list of SPIDs to kill - do it this way because otherwise .Net whines about a datareader being open
                using (SqlCommand cmd = dest.CreateCommand())
                {
                    cmd.CommandText = sql;

                    act = "getting sessions to kill";
                    SqlDataReader rdr = cmd.ExecuteReader();
                    if (rdr.HasRows)
                    {
                        act = "getting SPID";
                        while (rdr.Read())
                        {
                            int spid;
                            act = String.Format("converting '{0}' to an INT", rdr["spid"].ToString());
                            spid = int.Parse(rdr["spid"].ToString());
                            spids.Add(spid);
                        } // while
                    } // if: are there rows?
                    act = "closing reader";
                    rdr.Close();
                } // SqlCommand: cmd

                // Now kill each one
                using (SqlCommand killcmd = dest.CreateCommand())
                {
                    foreach (int spid in spids)
                    {
                        killcmd.CommandText = string.Format("KILL {0}",spid);
                        act = String.Format("killing session {0}", spid);
                        DebugMessage("\t" + act);
                        killcmd.ExecuteNonQuery();
                    } // foreach: spid
                } // SqlCommand: killcmd

/*              // This is how I wanted to do it, but apparently you cannot use parameterization when calling KILL
                using (SqlCommand killcmd = dest.CreateCommand())
                {
                    killcmd.CommandText = "KILL @spid";
                    killcmd.Parameters.Add(new SqlParameter("@spid", System.Data.SqlDbType.Int));
                    act = "preparing kill command";
                    killcmd.Prepare();

                    foreach(int spid in spids)
                    {
                        act = string.Format("setting parameter value to {0}",spid);
                        killcmd.Parameters["@spid"].Value = spid;
                        act = "adding parameter to command";
                        act = String.Format("killing session {0}", spid);
                        DebugMessage("\t" + act);
                        killcmd.ExecuteNonQuery();  // Get an invalid syntax 'KILL @spid' error here
                    } // foreach: spid
                } // SqlCommand: killcmd
*/
            } // try
            catch (Exception ex)
            {
                DebugMessage(string.Format("Error {0}: {1}", act, ex.ToString()));
                ret = false;
            } // catch

            DebugMessage(string.Format("Kill = {0}", ret.ToString()));

            return ret;
        } // Kill

        /// <summary>Clean up after ourselves</summary>
        /// <param name="copy_from"></param>
        /// <param name="src_dbname"></param>
        /// <param name="save_to"></param>
        /// <param name="dest_dbname"></param>
        /// <remarks>If using COPY_FROM and SAVE_TO parameters, deletion of files is straightforward.  Otherwise, the BAK file may not be directly accessible by this utility; build, execute, and remove a CmdShell SQL Server job to take care of the file in that case.</remarks>
        private static void DoCleanUp(string[] locations, string src_dbname, SqlConnection src_instance)
        {
            string act = "";

            DebugMessage(String.Format("DoCleanUp({0},{1},{2}:{3})", string.Join("|", locations), src_dbname, src_instance.DataSource, src_instance.Database));
            if (copy_from != "")
            {
                try
                {
                    foreach (string path in locations)
                    {
                        if (path != "")
                        {
                            string tmp = string.Format(@"{0}\{1}.bak", path, src_dbname);
                            act = "deleting " + tmp;
                            DebugMessage(act);
                            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
                        } // if: path is not empty
                    } // foreach
                } // try
                catch (Exception ex)
                {
                    Message(String.Format("WARNING: Error {0}: {1}", act, ex.ToString()));
                } // catch
            }
            else   // Create a job because there's no way to get to the files directly
            {
                try
                {
                    string jobname = String.Concat(src_instance, System.Guid.NewGuid().ToString());

                    DebugMessage(string.Format("Creating job {0}", jobname));
                    act = "retrieving job spec";
                    string jobstr = Properties.Resources.cleanup_job;

                    // Build the delete commands
                    string tmp = "";
                    foreach (string path in locations)
                    {
                        tmp += string.Format(@"if exists {0}\{1}.bak del {0}\{1}.bak\n", path, src_dbname);
                    } // foreach

                    //Clean up source instance file - it's a shared location so there's no need to do a separate cleanup on the destination instance
                    act = "customizing T-SQL";
                    jobstr = jobstr.Replace("!INSTANCE!", src_instance.DataSource);
                    jobstr = jobstr.Replace("!JOBNAME!", jobname);
                    jobstr = jobstr.Replace("!FILESPEC!", tmp);

                    DebugMessage("Installing job...");
                    DebugMessage(jobstr);
                    act = "installing job";
                    using (SqlCommand cmd = src_instance.CreateCommand())
                    {
                        cmd.CommandText = jobstr;
                        cmd.ExecuteNonQuery();

                        DebugMessage("Running job...");
                        act = "running job";
                        cmd.CommandText = String.Format("exec msdb.dbo.sp_start_job @job_name='{0}'", jobname);
                        cmd.ExecuteNonQuery();
                        DebugMessage("Waiting 1 second...");
                        System.Threading.Thread.Sleep(1000);    // Wait a second to let the job finish

                        DebugMessage("Deleting job...");
                        act = "removing job " + jobname;
                        cmd.CommandText = String.Format("exec msdb.dbo.sp_delete_job @job_name='{0}'", jobname);
                        cmd.ExecuteNonQuery();
                    } // SqlCommand
                }
                catch (Exception ex)
                {
                    Message(String.Format("WARNING: Error {0}: {1}", act, ex.ToString()));
                }
            } // if..else: easy way or complicated way?

        } // DoCleanUp

        /// <summary>Copy a file from source to destination</summary>
        /// <param name="copy_from">Where the source file lives</param>
        /// <param name="src_dbname">The filename part of the source file; .BAK is added by this subroutine</param>
        /// <param name="copy_to">The destination path for the copy</param>
        /// <returns></returns>
        private static bool CopyFiles(string copy_from, string src_dbname, string copy_to)
        {
            DebugMessage(string.Format("CopyFiles({0},{1},{2})", copy_from, src_dbname, copy_to));
            bool ret = true;

            Message("Transferring files.  Note if this is a large database, it will take a while...");

            try
            {
                System.IO.File.Copy(
                    string.Format(@"{0}\{1}.BAK", copy_from, src_dbname),
                    string.Format(@"{0}\{1}.BAK", copy_to, src_dbname),
                    true    // Overwrite existing file
                    );
            } // try
            catch (Exception ex)
            {
                ret = false;
                Message(string.Format(@"Error copying from {0}\{1} to {2}\{1} : {3}", copy_from, src_dbname, copy_to, ex.ToString()));
            } // catch

            if (ret) Message("Transfer complete!");

            DebugMessage("CopyFiles=" + ret.ToString());
            return ret;
        } // CopyFiles

        #endregion 

        #region "Permission sets"
        /// <summary>Apply PIPES permissions to users of the new database</summary>
        /// <param name="con"></param>
        /// <param name="dest_dbname"></param>
        private static void ApplyPIPESperms(SqlConnection con, string dest_dbname)
        {
            DebugMessage(string.Format("ApplyPIPESperms({0},{1})", con.DataSource, dest_dbname));
            Message("\tApplying PIPES permissions");
            string act = "";
            try
            {
                act = "changing database to " + dest_dbname;
                con.ChangeDatabase(dest_dbname);

                using (SqlCommand cmd = con.CreateCommand())
                {
                    foreach (string sql in Properties.Resources.PIPESperms.Split('\n'))
                    {
                        if(!sql.StartsWith("--") && (sql.Trim() != ""))
                        {
                            act = "running " + sql;
                            //Message("\t\t" + act);
                            cmd.CommandText = sql;
                            DebugMessage(string.Format("\t{0}", sql));
                            cmd.ExecuteNonQuery();
                        } // if: is SQL a blank line or comment?
                    } // foreach
                } // using: SqlCommand
            } // try
            catch(Exception ex)
            {
                Message(string.Format("Error {0}: {1}", act, ex.ToString()));
            } // catch
        } // ApplyPIPESperms
        #endregion

        #region "Messaging"
        /// <summary>Debug messages</summary>
        /// <param name="msg"></param>
        private static void DebugMessage(string msg)
        {
            if (debug)
            {
                Console.WriteLine(msg);
                try
                {
                    System.IO.File.AppendAllText(logfile, string.Format("DEBUG: {0}{1}", msg, Environment.NewLine));
                } // try
                catch (Exception)
                {
                    // Do nothing - if there's an error logging debugging output another developer can do something here if they choose.
                } // catch
            } // if: debug mode on?
        } // DebugMessage

        /// <summary>Centralized messaging</summary>
        /// <param name="msg"></param>
        private static void Message(string msg)
        {
            Console.WriteLine(msg);

            try
            {
                System.IO.File.AppendAllText(logfile, String.Format("{0}{1}",msg,Environment.NewLine));
            }
            catch (Exception)
            {
                // Do nothing - if there's an error logging output another developer can do something here if they choose.
            } // catch
        } // Message

        /// <summary>Delete the old log file</summary>
        /// <param name="logfile"></param>
        private static void DeleteOldLog(string logfile)
        {
            try
            {
                if (System.IO.File.Exists(logfile)) System.IO.File.Delete(logfile);
            }
            catch(Exception ex)
            {
                Message(string.Format("Error deleting old log file {0}: {1}", logfile, ex.ToString()));
            }
        } // DeleteOldLog

        #endregion

        #region "DB copying"
        /// <summary>Take a backup of MDF (and LDF if applicable)</summary>
        /// <param name="src_con">Connection to the source instance</param>
        /// <param name="src_dbname">Name of the DB on the soruce instance to copy</param>
        /// <param name="save_to">Write the backup file(s) here from the perspective of the source instance</param>
        /// <param name="backuplog">1=there's a transaction log to include in the backup</param>
        /// <param name="files">List of files in the database, retrieved by calling GetFileList()</param>
        /// <returns>0=success, nonzero=error</returns>
        private static int GetSourceData(SqlConnection src_con, string src_dbname, string save_to, int backuplog, out List<stFileListEntry> files)
        {
            int ret = 0;    // Be optimistic!
            files = null;

            DebugMessage(string.Format("GetSourceData({0},{1},{2},{3})", src_con.DataSource, src_dbname, save_to, backuplog));

            bool ok = true;
            string sql = "";

            // Make a copy-only backup of the source database
            DebugMessage("Get copy of source DB (MDF)");
            sql = string.Format(
                @"BACKUP DATABASE [{0}] TO DISK = N'{2}\{0}.bak' WITH COPY_ONLY, FORMAT, INIT, NAME = N'{0}-Database copy to {1}', SKIP, NOREWIND, NOUNLOAD,  STATS = 10",
                src_dbname,     // 0
                dest_instance,  // 1 
                save_to         // 2
            );
            DebugMessage(sql);
            if (!nop) ok = RunSQL(src_con, sql);
            if (!ok) ret = 1;

            if (ok && (backuplog == 1))
            {
                DebugMessage("Get copy of source DB (LDF)");
                sql = string.Format(
                    @"BACKUP LOG [{0}] TO  DISK = N'{2}\{0}.bak' WITH  COPY_ONLY, NOFORMAT, NOINIT,  NAME = N'{0}-Database (t-logs) copy to {1}', SKIP, NOREWIND, NOUNLOAD,  STATS = 10",
                    src_dbname,     // 0
                    dest_instance,  // 1
                    save_to         // 2
                );
                DebugMessage(sql);
                if (!nop) ok = RunSQL(src_con, sql);
                if (!ok) ret = 2;
            } // if: okay and there's a transaction log?

            if (ok)
            {
                DebugMessage(string.Format(@"Call GetFileList({0},{1}\{2}.bak)", src_con.DataSource, save_to, src_dbname));

                // Getting the logical names this way avoids errors when in no operation mode because there won't be a backup file to read
                if (!nop) files = GetFileList(src_con, string.Format(@"{0}\{1}.bak", save_to, src_dbname)); // save_to
                DebugMessage(string.Format("GetFileList found {0} files",files.Count));

                if ((files !=null) && (files.Count == 0)) { 
                    ok = false;
                    ret = 3;    // File list not obtained successfully
                } // if..else: got file list?
            } // if: okay to obtain file list?

            if (nop) ret = 0;    // Force an all clear if we're running in no operation mode

            return ret;
        } // GetSourceData

        /// <summary>On the destination instance, restore the database from the backup files</summary>
        /// <param name="dest_con"></param>
        /// <param name="dest_dbname"></param>
        /// <param name="read_from"></param>
        /// <param name="backuplog"></param>
        /// <param name="bak_name"></param>
        /// <returns>0=success, nonzero=error code</returns>
        private static int PutDestData(SqlConnection dest_con, string dest_dbname, string read_from, int backuplog, string bak_name, List<stFileListEntry> filelist)
        {
            int ret = 0;
            string sql = "";

            DebugMessage(string.Format("PutDestData({0},{1},{2},{3},{4})", dest_con.DataSource, dest_dbname, read_from, backuplog, bak_name));

            Message("Writing destination database");
            try
            {
                Message("\tDatabase");
                //SqlCommand cmd = dest_con.CreateCommand();
                //cmd.CommandText = string.Format(@"RESTORE DATABASE[{0}] FROM DISK = N'{6}\{3}.bak' WITH FILE = 1, MOVE N'{4}' TO N'{1}{0}.mdf', MOVE N'{5}' TO N'{2}{0}_log.ldf', NORECOVERY,  NOUNLOAD,  REPLACE,  STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname,logicalname_d,logicalname_l,holdingpath);
                //cmd.ExecuteNonQuery();

                sql = string.Format(@"RESTORE DATABASE[{0}] 
                    FROM DISK = N'{2}\{1}.bak' WITH FILE = 1, 
                    [!MOVES!],
                    NORECOVERY,  NOUNLOAD,  [!REPL!]STATS = 5",

                    dest_dbname,        // 0
                    bak_name,           // 1
                    read_from           // 2
                    );
                if (dest_overwrite)
                {    // Don't use REPLACE if we're not actually replacing a database!
                    sql = sql.Replace("[!REPL!]", "REPLACE, ");
                }
                else
                {
                    sql = sql.Replace("[!REPL!]", "");
                } // if..else: use REPLACE?
                sql = sql.Replace("[!MOVES!]", FileMoves(filelist, MDFdir, LDFdir,dest_dbname));

                if (backuplog != 1) sql = sql.Replace(", NORECOVERY,", ", RECOVERY,"); // No transaction log, so this is the only RESTORE to run
                DebugMessage(sql);
                if (!nop) RunSQL(dest_con, sql); // Run the RESTORE DATABASE

                if (backuplog == 1)
                {
                    Message("\tTransaction log");
                    sql = string.Format(@"RESTORE LOG [{0}] FROM  DISK = N'{1}\{2}.bak' WITH  FILE = 1, NOUNLOAD,  RECOVERY, STATS = 5",
                        dest_dbname,    // 0 
                        read_from,      // 1
                        bak_name        // 2
                        );
                    DebugMessage(sql);
                    if (!nop) RunSQL(dest_con, sql);  // Run the RESTORE LOG
                    //cmd.CommandText = string.Format(@"RESTORE LOG [{0}] FROM  DISK = N'{4}\{3}.bak' WITH  FILE = 1, NOUNLOAD,  RECOVERY, STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname, holdingpath);
                    //cmd.ExecuteNonQuery();
                } // if: has transaction log?

                DebugMessage("\tSet multiuser");
                if (!nop) RunSQL(dest_con, string.Format(@"ALTER DATABASE[{0}] SET MULTI_USER", dest_dbname));
                //cmd.CommandText = string.Format(@"ALTER DATABASE[{0}] SET MULTI_USER", dest_dbname);
                //cmd.ExecuteNonQuery();

            } // try
            catch (Exception ex)
            {
                ret = 100;  // General exception
                Message("Error restoring destination database: " + ex.ToString());
            } // catch

            return ret;
        } // PutDestData
        #endregion

        #region "DB operations"

        /// <summary>Connect to the source and destination instances</summary>
        /// <returns>TRUE: success, FALSE: error</returns>
        /// <remarks>Uses the context of the user running this utility if usernames and passwords aren't specified</remarks>
        private static bool SqlConnect()
        {
            bool ret = true;
            string connstr = "";

            try
            {
                Message("Connecting to source instance");
                if (src_user == "")
                {
                    connstr = string.Format("Server={0};Integrated Security=true;", src_instance);
                }
                else
                {
                    connstr = string.Format("Server={0};UID={1};PWD={2};", src_instance, src_user, "src_pass");
                } // if..else: use username/password?
                src_con = new SqlConnection(connstr);
                src_con.Open();
            }
            catch (Exception ex)
            {
                ret = false;
                Message("Error connecting to source instance: " + ex.ToString());
            }

            if (ret)
            {
                try
                {
                    Message("Connecting to destination instance");
                    if (dest_user == "")
                    {
                        connstr = string.Format("Server={0};Integrated Security=true;", dest_instance);
                    }
                    else
                    {
                        connstr = string.Format("Server={0};UID={1};PWD={2};", dest_instance, dest_user, "dest_pass");
                    } // if..else: use username/password?
                    dest_con = new SqlConnection(connstr);
                    dest_con.Open();
                }
                catch (Exception ex)
                {
                    ret = false;
                    Message("Error connecting to destination instance: " + ex.ToString());
                }
            } // if: connected to source instance?

            return ret;
        } // SqlConnect

        /// <summary>Execute the given SQL, with the expectation that it doesn't return a result set</summary>
        /// <param name="con">SQL instance</param>
        /// <param name="sql">The SQL to execute</param>
        /// <param name="type">Specify either SQL command (default), or stored procedure</param>
        /// <returns>Success: TRUE, errors: FALSE</returns>
        private static bool RunSQL(SqlConnection con, string sql, System.Data.CommandType type = System.Data.CommandType.Text)
        {
            DebugMessage(string.Format("RunSQL({0},{1})", con.DataSource, sql));
            bool ret = true;

            try
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandType = type;
                    cmd.CommandTimeout = 600;   // 10 minutes
                    DebugMessage(string.Format("\tCommandTimeout = {0}", cmd.CommandTimeout));
                    cmd.ExecuteNonQuery();
                } // using: SqlCommand
            }
            catch (Exception ex)
            {
                Message(string.Format("Error running {0}\n{1}", sql, ex.ToString()));
                ret = false;
            }
            DebugMessage(string.Format("RunSQL = {0}", ret));
            return ret;
        } // RunSQL

        /// <summary>Drop the specified database on the given instance</summary>
        /// <param name="instance"></param>
        /// <param name="dbname"></param>
        private static bool DropDestDB(SqlConnection instance, string dbname)
        {
            bool ret = true;
            try
            {
                SqlCommand cmd = instance.CreateCommand();

                Message("\tclearing backup history");
                cmd.CommandText = string.Format("EXEC msdb.dbo.sp_delete_database_backuphistory @database_name=N'{0}'", dbname);
                cmd.CommandType = System.Data.CommandType.StoredProcedure;

                Message("\tsetting single user mode");
                cmd.CommandText = string.Format("ALTER DATABASE [{0}] SET SINGLE_USER;", dbname);
                cmd.CommandType = System.Data.CommandType.Text;
                cmd.ExecuteNonQuery();

                Message("\tdropping database");
                cmd.CommandText = string.Format("DROP DATABASE [{0}]", dbname);
                cmd.CommandType = System.Data.CommandType.Text;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Message("Error dropping destination database: " + ex.ToString());
                ret = false;
            }

            return ret;
        } // DropDestDB

        /// <summary>Get default MDF and LDF directories</summary>
        /// <param name="con">Connection to the SQL Server instance</param>
        /// <returns>TRUE=success, FALSE=error</returns>
        private static bool GetDefaultDirs(SqlConnection con)
        {
            DebugMessage(string.Format("GetDefaultDirs({0})", con.DataSource));
            bool ret = true;

            try
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = "SELECT DataPath = CONVERT(sysname, SERVERPROPERTY('InstanceDefaultDataPath')),LogPath = CONVERT(sysname, SERVERPROPERTY('InstanceDefaultLogPath'))";
                    SqlDataReader rdr = cmd.ExecuteReader();
                    if (rdr.HasRows)
                    {
                        if (rdr.Read())
                        {
                            MDFdir = rdr["DataPath"].ToString();
                            LDFdir = rdr["LogPath"].ToString();
                        }
                    }
                    rdr.Close();
                } // using: SqlCommand
            } // try
            catch (Exception ex)
            {
                Message(string.Format("Error getting default directories: {0}", ex.ToString()));
                ret = false;
            } // catch
            DebugMessage(string.Format("GetDefaultDirs = {0}", ret.ToString()));
            return ret;
        } // GetDefaultDirs

        /// <summary>Check if the specified database has a transaction log to include in the backup</summary>
        /// <param name="con">Connection to the database instance</param>
        /// <param name="src_dbname">Name of the database to check for a transaction log</param>
        /// <returns>1=yes, it has a transaction log, 0=no, there's no transaction log, -1=error</returns>
        private static int HasTLog(SqlConnection con, string src_dbname)
        {
            DebugMessage(string.Format("HasTLog({0},{1})", con.DataSource, src_dbname));
            int ret = 0;    // Assume simple recovery model

            try
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = string.Format("SELECT recovery_model_desc FROM master.sys.databases WHERE name='{0}'", src_dbname);
                    object value = cmd.ExecuteScalar();
                    switch (value.ToString().ToUpper()) // Use a switch to make handling multiple possible values and future expansion easier
                    {
                        case "BULK_LOGGED":
                        case "FULL":
                            ret = 1;
                            break;
                    } // switch
                } // using: SqlCommand
            } // try
            catch (Exception ex)
            {
                Message(string.Format("Error checking database recovery model: {0}", ex.ToString()));
                ret = -1;
            } // catch

            DebugMessage(string.Format("HasTLog = {0}", ret));

            return ret;
        } // HasTLog

        /// <summary>Check if the source database exists</summary>
        /// <param name="con">Connection to the source MSSQL instance</param>
        /// <param name="dbname">The name of the source database</param>
        /// <returns>TRUE: database exists, FALSE: database doesn't exist or there was an error</returns>
        private static bool SourceDBexists(SqlConnection con, string dbname)
        {
            bool ret = true;    // Be optimistic!

            DebugMessage(string.Format("SourceDBexists({0},{1})", con.DataSource, dbname));

            try
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = string.Format("select count([name]) from sys.databases where [name] = '{0}'", dbname);
                    object count = cmd.ExecuteScalar();
                    if (int.Parse(count.ToString()) == 0) ret = false;
                } // using: SqlCommand

            } // try
            catch (Exception ex)
            {
                ret = false;
                Message(string.Format("Error checking if database exists: {0}", ex.ToString()));
            } // catch

            DebugMessage(string.Format("SourceDBexists = {0}", ret.ToString()));

            return ret;
        } // SourceDBexists

        /// <summary>Returns the list of files that make up the database</summary>
        /// <param name="con">A connection to a SQL instance to process the request</param>
        /// <param name="backupfile">The backup file from which to obtain the logical names</param>
        /// <returns>Success: data-logical-name|transaction-log-logical-name  Error: a blank string</returns>
        /// <remarks>A database can contain more than just data and transaction logs, such as full text indexes and filestream/filetable/in-memory OLTP containers</remarks>
        private static List<stFileListEntry> GetFileList(SqlConnection con, string backupfile)
        {
            List<stFileListEntry> ret = new List<stFileListEntry>();

            DebugMessage(string.Format("GetLogicalNames({0},{1})", con.DataSource, backupfile));

            try
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = string.Format("RESTORE FILELISTONLY FROM DISK='{0}'", backupfile);
                    SqlDataReader rdr = cmd.ExecuteReader();
                    if (rdr.HasRows)
                    {
                        while (rdr.Read())
                        {
                            stFileListEntry entry = new stFileListEntry();
                            entry.filegroupname = rdr["FileGroupName"].ToString();
                            entry.logicalname = rdr["LogicalName"].ToString();
                            entry.physicalname = rdr["PhysicalName"].ToString();
                            entry.type = rdr["Type"].ToString();
                            ret.Add(entry);
                        } // while
                    } // if: rows returned?
                    rdr.Close();
                } // using: SqlCommand
            } // try
            catch (Exception ex)
            {
                Message(string.Format("Error getting file list: {0}", ex.ToString()));
            } // catch

            DebugMessage(string.Format("GetFileList found {0} files", ret.Count));
            return ret;
        } // GetFileList

        /// <summary>Build the MOVE clauses for the RESTORE statement</summary>
        /// <param name="filelist">The list of files in the database</param>
        /// <param name="mdfdir">Where MDF (and other non transaction log) files live in the target host</param>
        /// <param name="ldfdir">Where transaction log files live in the target host</param>
        /// <returns>success: a set of MOVE clauses; error: empty string</returns>
        private static string FileMoves(List<stFileListEntry> filelist, string mdfdir, string ldfdir, string dbname)
        {
            // !!! Change the filename of each file to avoid collisions - use the destination DB name as a base
            string ret = "";
            List<string> moves = new List<string>();    // Holds all the MOVE clauses
            string move = "";   // Holds the MOVE clause while it's being built
            string physicalname = "";   // The full path of the file on the target server

            DebugMessage(string.Format("FileMoves({0} files, {1}, {2})", filelist.Count, mdfdir, ldfdir));

            foreach(stFileListEntry file in filelist)
            {
                // Build the full path for the file on the DESTINATION host/instance
                physicalname = string.Format("{0}-{1}",
                    dbname,
                    System.IO.Path.GetFileName(file.physicalname));   // Isolate the MDF/LDF/NDF/whatever file and make it unique
                switch (file.type)
                {
                    case "L":   // It's a transaction log
                        physicalname = System.IO.Path.Combine(ldfdir, physicalname);
                        break;
                    default:    // It's something else: rowdata, filestream, etc.
                        physicalname = System.IO.Path.Combine(mdfdir, physicalname);
                        break;
                } // switch: type of file
                
                move = string.Format("MOVE N'{0}' TO N'{1}'", file.logicalname, physicalname);
                moves.Add(move);    // Add the new MOVE clause to the list
            } // foreach

            ret = string.Join(", ", moves.ToArray());    // Assemble the MOVE clauses into one string

            DebugMessage(string.Format("FileMoves = {0}", ret));
            return ret;
        } // FileMoves
        #endregion

        #region "Deprecated"
        /*
        /// <summary>Copies the database</summary>
        /// <param name="src_con"></param>
        /// <param name="src_dbname"></param>
        /// <param name="dest_con"></param>
        /// <param name="dest_dbname"></param>
        /// <returns>0=success, non-zero indicates an error</returns>
        private static int CopyDatabase(SqlConnection src_con, string src_dbname, SqlConnection dest_con, string dest_dbname)
        {
            int ret = 0;    // Be optimistic!

            DebugMessage(string.Format("CopyDatabase({0},{1},{2},{3})", src_con.DataSource, src_dbname, dest_con.DataSource, dest_dbname));
            bool ok = true;

            string sql = "";

            // Make a copy-only backup of the source database
            Message("Get copy of source DB (MDF)");
            if (!RunSQL(src_con, string.Format(@"BACKUP DATABASE [{0}] TO DISK = N'{2}\{0}.bak' WITH COPY_ONLY, FORMAT, INIT, NAME = N'{0}-Database copy to {1}', SKIP, NOREWIND, NOUNLOAD,  STATS = 10", src_dbname, dest_instance, save_to)))
            {
                ok = false;
                ret = 1;
            } // if: database backup successful?


            if (ok && (backuplog == 1))
            {
                Message("Get copy of source DB (LDF)");
                if (!RunSQL(src_con, string.Format(@"BACKUP LOG [{0}] TO  DISK = N'{2}\{0}.bak' WITH  COPY_ONLY, NOFORMAT, NOINIT,  NAME = N'{0}-Database copy to {1}', SKIP, NOREWIND, NOUNLOAD,  STATS = 10", src_dbname, dest_instance, save_to)))
                {
                    ok = false;
                    ret = 2;
                } // if: log backup ok?

            } // if: okay and there's a transaction log?

            if (ok)
            {
                string tmp = GetLogicalNames(src_con, string.Format(@"{0}\{1}.bak", save_to, src_dbname));
                if (tmp != "")
                {
                    if (tmp.Contains("|"))
                    {
                        logicalname_d = tmp.Split('|')[0];
                        logicalname_l = tmp.Split('|')[1];
                    }
                    else
                    {
                        logicalname_d = tmp;
                    } // if..else: how many logical names?
                }
                else
                {
                    ok = false;
                    ret = 3;    // Logical names not obtained successfully
                } // if..else: got logical names?
            } // if: okay to obtain logical names?

            if (ok)
            {
                Message("Bringing copy to destination instance");
                try
                {
                    Message("\tDatabase");
                    //SqlCommand cmd = dest_con.CreateCommand();
                    //cmd.CommandText = string.Format(@"RESTORE DATABASE[{0}] FROM DISK = N'{6}\{3}.bak' WITH FILE = 1, MOVE N'{4}' TO N'{1}{0}.mdf', MOVE N'{5}' TO N'{2}{0}_log.ldf', NORECOVERY,  NOUNLOAD,  REPLACE,  STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname,logicalname_d,logicalname_l,holdingpath);
                    //cmd.ExecuteNonQuery();

                    sql = string.Format(@"RESTORE DATABASE[{0}] FROM DISK = N'{6}\{3}.bak' WITH FILE = 1, MOVE N'{4}' TO N'{1}{0}.mdf', MOVE N'{5}' TO N'{2}{0}_log.ldf', NORECOVERY,  NOUNLOAD,  !REPL!STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname, logicalname_d, logicalname_l, read_from);
                    if (dest_overwrite)
                    {    // Don't use REPLACE if we're not actually replacing a database!
                        sql = sql.Replace("!REPL!", "REPLACE, ");
                    }
                    else
                    {
                        sql = sql.Replace("!REPL!", "");
                    } // if..else: use REPLACE?
                    if (backuplog != 1) sql = sql.Replace(", NORECOVERY,", ", RECOVERY,"); // No transaction log, so this is the only RESTORE to run
                    RunSQL(dest_con, sql);

                    if (backuplog == 1)
                    {
                        Message("\tTransaction log");
                        RunSQL(dest_con, string.Format(@"RESTORE LOG [{0}] FROM  DISK = N'{4}\{3}.bak' WITH  FILE = 1, NOUNLOAD,  RECOVERY, STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname, read_from));
                        //cmd.CommandText = string.Format(@"RESTORE LOG [{0}] FROM  DISK = N'{4}\{3}.bak' WITH  FILE = 1, NOUNLOAD,  RECOVERY, STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname, holdingpath);
                        //cmd.ExecuteNonQuery();
                    } // if: has transaction log?

                    Message("\tSet multiuser");
                    RunSQL(dest_con, string.Format(@"ALTER DATABASE[{0}] SET MULTI_USER", dest_dbname));
                    //cmd.CommandText = string.Format(@"ALTER DATABASE[{0}] SET MULTI_USER", dest_dbname);
                    //cmd.ExecuteNonQuery();

                    // Clean up after ourselves
                    System.IO.File.Delete(string.Format(@"{0}\{1}.bak", save_to, src_dbname));
                    if (System.IO.File.Exists(string.Format(@"{0}\{1}.bak", read_from, dest_dbname)))
                        System.IO.File.Delete(string.Format(@"{0}\{1}.bak", read_from, dest_dbname));

                    // Extras
                    if (setPIPESperms) ApplyPIPESperms(dest_con, dest_dbname);

                } // try
                catch (Exception ex)
                {
                    ret = 100;  // General exception
                    Message("Error restoring destination database: " + ex.ToString());
                } // catch
            } // if: is ok?

            return ret;
        } // CopyDatabase
        */
        #endregion


    } // class
} // namespace
