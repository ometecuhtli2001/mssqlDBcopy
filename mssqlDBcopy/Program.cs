using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.SqlClient;

namespace mssqlDBcopy
{
    class Program
    {
        // Source and destination information
        private static string src_instance;
        private static string src_dbname;
        private static string src_user = "";   // Blank if using current user context (default)
        private static string src_pass = "";
        private static string dest_instance;
        private static string dest_dbname;
        private static string dest_user = "";   // Blank if using current user context (default)
        private static string dest_pass = "";

        private static SqlConnection src_con;   // Source instance connection
        private static SqlConnection dest_con;  // Target instance connection

        private static string MDFdir = "";      // Where source MDF file(s) live
        private static string LDFdir = "";      // Where source LDF file(s) live
        private static int backuplog = 0;       // Flag indicating if there's a transaction log for the specified DB
        private static string logicalname_d = "";   // Logical name of database file
        private static string logicalname_l = "";   // Logcial name of transaction log file

        /* Separate variables for the holding path because the source and destination instances can refer to the same place 
         * in different ways.  Note that if an instance is on Linux, and the holding area is not local to that box, it must
         * already be mounted before running this utility.
         */
        private static string holdingpath = "";     // Where to put the backup files during transfer from source -> destination
        private static string src_holdingpath = "";     // How to reference holding area in source instance
        private static string dest_holdingpath = "";    // How to reference holding area in destination instance


        private static bool setPIPESperms = false;
        private static bool dest_overwrite = false;    // Safety first!
        private static bool debug = false;             // Debug mode (defaults to off)

        private static bool nop = false;    // If true, do nothing - just parse command line args and exit
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Please specify a source instance and database followed by a target instance and database. Options come after that.");
                Console.WriteLine("\tmssqldbcopy source-instance:database target-instance:database [/REPLACE|/PIPESPERMS][/SRC_CREDS:user:pass][/DEST_CREDS:user:pass][[/PATH=holding-path]|[/SRC_PATH=source-holding-path /DEST_PATH=destination-holding-path]]");
                Console.WriteLine();
                Console.WriteLine("Note if you specify /PATH applied to both source and destination, so you cannot specify /SRC_PATH or /DEST_PATH with /PATH.  Likewise, /SRC_PATH and /DEST_PATH go together - you must specify both, and then you cannot use /PATH.");
                Console.WriteLine();
            }
            else
            {
                string act = "";
                // Get settings from environment variables
                try
                {
                    act = "MSSQLDBCOPY_HOLDINGPATH";
                    if (Environment.GetEnvironmentVariable("MSSQLDBCOPY_HOLDINGPATH") != null) holdingpath = Environment.GetEnvironmentVariable("MSSQLDBCOPY_HOLDINGPATH");
                    act = "MSSQLDBCOPY_SRC_HOLDINGPATH";
                    if (Environment.GetEnvironmentVariable("MSSQLDBCOPY_SRC_HOLDINGPATH") != null) src_holdingpath = Environment.GetEnvironmentVariable("MSSQLDBCOPY_SRC_HOLDINGPATH");
                    act = "MSSQLDBCOPY_DEST_HOLDINGPATH";
                    if (Environment.GetEnvironmentVariable("MSSQLDBCOPY_DEST_HOLDINGPATH") != null) dest_holdingpath = Environment.GetEnvironmentVariable("MSSQLDBCOPY_DEST_HOLDINGPATH");
                }
                catch (Exception ex)
                {
                    Message(string.Format("WARNING: There was a problem checking on or retrieving the {0} environment variable: {1}",act, ex.ToString()));
                }

                // Now parse command line arguments, which can override environment variables
                ParseCommandLine(args);


                /* Go through settings (not that they should be set) and check for sanity */

                // Check holding path settings - SRC_PATH and DEST_PATH will override PATH and environment variables
                if ((holdingpath == "") && ((src_holdingpath =="") || (dest_holdingpath =="")))
                {
                    Message("ERROR: Please use either /PATH or both /SRC_PATH and /DEST_PATH to specify a holding path for the files to stay during the transfer.");
                    nop = true;
                } // if: is there a holding path?

                // No source and destination holding paths specified, so use PATH for both
                if ((holdingpath != "") && (src_holdingpath == "") && (dest_holdingpath == ""))
                {
                    if (holdingpath.EndsWith(@"\")) holdingpath = holdingpath.Trim('\\');   // Remove trailing back slash from holding path
                    src_holdingpath = holdingpath;
                    dest_holdingpath = holdingpath;
                } // if: is holdingpath empty?

                DebugMessage(string.Format("SRC: user={0} pass={1} instance={2}  DB={3}", src_user, src_pass, src_instance, src_dbname));
                DebugMessage(string.Format("DEST: user={0} pass={1} instance={2}  DB={3}", dest_user, dest_pass, dest_instance, dest_dbname));
                DebugMessage(string.Format("Misc: replace={0} PIPES perms={1} NOP={2}", dest_overwrite.ToString(), setPIPESperms.ToString(), nop.ToString()));

                if (!nop)
                {
                    if (SqlConnect())
                    {
                        backuplog = HasTLog(src_con, src_dbname);

                        if (backuplog != -1)
                        {
                            if (GetDefaultDirs(dest_con))
                            {
                                if (dest_overwrite)
                                {
                                    if (DropDestDB(dest_con, dest_dbname)) CopyDatabase(src_con, src_dbname, dest_con, dest_dbname);
                                }
                                else
                                {
                                    CopyDatabase(src_con, src_dbname, dest_con, dest_dbname);
                                } // if..else: overwrite destination?
                            } // if: got default directories on dest instance?
                        } // if: got transaction log info?
                    } // if: connected?
                } // if: to do stuff or not to do stuff
            } // if..else: proper parameter count?

            Console.WriteLine("Done!");
            Console.ReadKey();
        } // Main

        /// <summary>Debug messages</summary>
        /// <param name="msg"></param>
        private static void DebugMessage(string msg)
        {
            if(debug) Console.WriteLine(msg);
        } // DebugMessage

        /// <summary>Parse command line arguments</summary>
        /// <param name="args">Array of command line arguments</param>
        private static void ParseCommandLine(string[] args)
        {
            src_instance = args[0].Split(':')[0];
            src_dbname = args[0].Split(':')[1];
            dest_instance = args[1].Split(':')[0];
            dest_dbname = args[1].Split(':')[1];

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
                        catch(Exception ex)
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
                        nop = true;
                        break;
                    case "/DEBUG":
                        debug = true;
                        Console.WriteLine("Debug mode is now on.");
                        break;
                } // switch: sw

                foreach(string arg in new string[] {"/PATH","/SRC_PATH","/DEST_PATH" })
                {
                    if (sw.ToUpper().StartsWith(arg))
                    {
                        switch (arg)
                        {
                            case "/PATH": holdingpath = sw.Split('=')[1];
                                break;
                            case "/SRC_PATH":
                                src_holdingpath = sw.Split('=')[1];
                                break;
                            case "/DEST_PATH":
                                dest_holdingpath = sw.Split('=')[1];
                                break;
                        } // switch: which path
                    } // if: holding path specified?
                } // foreach: iterate over arguments which use an = instead of a colon
            } // foreach: iterate over parameters
        } // ParseCommandLine

        /// <summary>Returns the logical names for database and transaction log</summary>
        /// <param name="con">A connection to a SQL instance to process the request</param>
        /// <param name="backupfile">The backup file from which to obtain the logical names</param>
        /// <returns>Success: data-logical-name|transaction-log-logical-name  Error: a blank string</returns>
        private static string GetLogicalNames(SqlConnection con, string backupfile)
        {
            string ret = "";
            string d = "";
            string l = "";

            DebugMessage(string.Format("GetLogicalNames({0},{1})", con.DataSource, backupfile));

            try
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = string.Format("RESTORE FILELISTONLY FROM DISK='{0}'",backupfile);
                    SqlDataReader rdr = cmd.ExecuteReader();
                    if (rdr.HasRows)
                    {
                        while (rdr.Read())
                        {
                            switch (rdr["type"])
                            {
                                case "D":
                                    d = rdr["LogicalName"].ToString();
                                    break;
                                case "L":
                                    l = rdr["LogicalName"].ToString();
                                    break;
                            } // switch: type
                        } // while
                    } // if: rows returned?
                    rdr.Close();
                } // using: SqlCommand

                ret= (l != "") ? d+"|"+ l : d;  // If there's no log, just return the logical name for the data
            } // try
            catch (Exception ex)
            {
                Message(string.Format("Error getting logical names: {0}", ex.ToString()));
            } // catch

            DebugMessage(string.Format("GetLogicalNames = {0}", ret));
            return ret;
        } // GetLogicalNames

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

        /// <summary>Get default MDF and LDF directories</summary>
        /// <param name="con"></param>
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
        }

        /// <summary>Check if the specified database needs to have a transaction log backup</summary>
        /// <param name="con"></param>
        /// <param name="src_dbname"></param>
        /// <returns>1=yes, it has a transaction log, 0=no, there's no transaction log, -1=error</returns>
        private static int HasTLog(SqlConnection con, string src_dbname)
        {
            DebugMessage(string.Format("HasTLog({0},{1})", con.DataSource, src_dbname));
            int ret = 0;    // Assume simple recovery model

            try
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = string.Format("SELECT recovery_model_desc FROM master.sys.databases WHERE name='{0}'",src_dbname);
                    object value=cmd.ExecuteScalar();
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

        /// <summary>Execute the given SQL, with the expectation that it doesn't return a result set</summary>
        /// <param name="con">SQL instance</param>
        /// <param name="sql">The SQL to execute</param>
        /// <param name="type">Specify either SQL command (default), or stored procedure</param>
        /// <returns>Success: TRUE, errors: FALSE</returns>
        private static bool RunSQL(SqlConnection con, string sql, System.Data.CommandType type=System.Data.CommandType.Text)
        {
            DebugMessage(string.Format("RunSQL({0},{1})", con.DataSource, sql.Substring(0,20)));
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
            catch(Exception ex)
            {
                Message(string.Format("Error running {0}\n{1}", sql, ex.ToString()));
                ret = false;
            }
            DebugMessage(string.Format("RunSQL = {0}", ret));
            return ret;
        } // RunSQL

        /// <summary>Copies the database</summary>
        /// <param name="src_con"></param>
        /// <param name="src_dbname"></param>
        /// <param name="dest_con"></param>
        /// <param name="dest_dbname"></param>
        private static void CopyDatabase(SqlConnection src_con, string src_dbname, SqlConnection dest_con, string dest_dbname)
        {
            DebugMessage(string.Format("CopyDatabase({0},{1},{2},{3})", src_con.DataSource,src_dbname,dest_con.DataSource,dest_dbname));
            bool ok = true;

            string sql = "";

            // Make a copy-only backup of the source database
            Message("Get copy of source DB (MDF)");
            if (!RunSQL(src_con, string.Format(@"BACKUP DATABASE [{0}] TO DISK = N'{2}\{0}.bak' WITH COPY_ONLY, FORMAT, INIT, NAME = N'{0}-Database copy to {1}', SKIP, NOREWIND, NOUNLOAD,  STATS = 10", src_dbname, dest_instance,src_holdingpath)))
                ok = false;

            if (ok && (backuplog == 1))
            {
                Message("Get copy of source DB (LDF)");
                if (!RunSQL(src_con, string.Format(@"BACKUP LOG [{0}] TO  DISK = N'{2}\{0}.bak' WITH  COPY_ONLY, NOFORMAT, NOINIT,  NAME = N'{0}-Database copy to {1}', SKIP, NOREWIND, NOUNLOAD,  STATS = 10", src_dbname, dest_instance, src_holdingpath)))
                    ok = false;
            } // if: okay and there's a transaction log?

            if (ok)
            {
                string tmp = GetLogicalNames(src_con, string.Format(@"{0}\{1}.bak",src_holdingpath, src_dbname));
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

                    sql = string.Format(@"RESTORE DATABASE[{0}] FROM DISK = N'{6}\{3}.bak' WITH FILE = 1, MOVE N'{4}' TO N'{1}{0}.mdf', MOVE N'{5}' TO N'{2}{0}_log.ldf', NORECOVERY,  NOUNLOAD,  !REPL!STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname, logicalname_d, logicalname_l, dest_holdingpath);
                    if (dest_overwrite)
                    {    // Don't use REPLACE if we're not actually replacing a database!
                        sql = sql.Replace("!REPL!", "REPLACE, ");
                    } else
                    {
                        sql = sql.Replace("!REPL!", "");
                    } // if..else: use REPLACE?
                    if (backuplog != 1) sql = sql.Replace(", NORECOVERY,", ", RECOVERY,"); // No transaction log, so this is the only RESTORE to run
                    RunSQL(dest_con, sql);

                    if (backuplog == 1)
                    {
                        Message("\tTransaction log");
                        RunSQL(dest_con, string.Format(@"RESTORE LOG [{0}] FROM  DISK = N'{4}\{3}.bak' WITH  FILE = 1, NOUNLOAD,  RECOVERY, STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname, dest_holdingpath));
                        //cmd.CommandText = string.Format(@"RESTORE LOG [{0}] FROM  DISK = N'{4}\{3}.bak' WITH  FILE = 1, NOUNLOAD,  RECOVERY, STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname, holdingpath);
                        //cmd.ExecuteNonQuery();
                    } // if: has transaction log?

                    Message("\tSet multiuser");
                    RunSQL(dest_con, string.Format(@"ALTER DATABASE[{0}] SET MULTI_USER", dest_dbname));
                    //cmd.CommandText = string.Format(@"ALTER DATABASE[{0}] SET MULTI_USER", dest_dbname);
                    //cmd.ExecuteNonQuery();

                    // Clean up after ourselves
                    System.IO.File.Delete(string.Format(@"{0}\{1}.bak",src_holdingpath, src_dbname));
                    if(System.IO.File.Exists(string.Format(@"{0}\{1}.bak", dest_holdingpath, dest_dbname)))
                        System.IO.File.Delete(string.Format(@"{0}\{1}.bak", dest_holdingpath, dest_dbname));

                    // Extras
                    if (setPIPESperms) ApplyPIPESperms(dest_con, dest_dbname);

                } // try
                catch (Exception ex)
                {
                    Message("Error restoring destination database: " + ex.ToString());
                } // catch
            } // if: is ok?
        } // CopyDatabase

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
                    connstr = string.Format("Server={0};UID={1};PWD={2};", src_instance, src_user, src_pass);
                } // if..else: use username/password?
                src_con = new SqlConnection(connstr);
                src_con.Open();
            }
            catch(Exception ex)
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
                        connstr = string.Format("Server={0};UID={1};PWD={2};", dest_instance, dest_user, dest_pass);
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

        /// <summary>Centralized messaging</summary>
        /// <param name="msg"></param>
        private static void Message(string msg)
        {
            Console.WriteLine(msg);
        }
    } // class
} // namespace
