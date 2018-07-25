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

        private static SqlConnection src_con;
        private static SqlConnection dest_con;

        private static string MDFdir = "";
        private static string LDFdir = "";
        private static int backuplog = 0;
        private static string logicalname_d = "";   // Logical name of database file
        private static string logicalname_l = "";   // Logcial name of transaction log file

        private static string holdingpath = @"\\devwebprivate3\sQL_Share\OUT";     // Where to put the backup files during transfer from source -> destination

        private static bool setPIPESperms = false;
        private static bool dest_overwrite = false;    // Safety first!

        private static bool nop = false;    // If true, do nothing - just parse command line args and exit
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Please specify a source instance and database followed by a target instance and database. Options come after that.");
                Console.WriteLine("\tmssqldbcopy source-instance:database target-instance:database [/REPLACE|/PIPESPERMS][/SRC_CREDS:user:pass][/DEST_CREDS:user:pass][/PATH=holding-path]");
                Console.WriteLine();
            }
            else
            {
                ParseCommandLine(args);

                Console.WriteLine(string.Format("SRC: user={0} pass={1} instance={2}  DB={3}", src_user, src_pass, src_instance, src_dbname));
                Console.WriteLine(string.Format("DEST: user={0} pass={1} instance={2}  DB={3}", dest_user, dest_pass, dest_instance, dest_dbname));
                Console.WriteLine(string.Format("Misc: replace={0} PIPES perms={1} NOP={2}", dest_overwrite.ToString(), setPIPESperms.ToString(), nop.ToString()));

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
                } // switch: sw

                if (sw.ToUpper().StartsWith("/PATH"))
                {
                    holdingpath = sw.Split('=')[1];
                } // if: holding path specified?
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

            return ret;
        } // GetLogicalNames

        /// <summary>Apply PIPES permissions to users of the new database</summary>
        /// <param name="con"></param>
        /// <param name="dest_dbname"></param>
        private static void ApplyPIPESperms(SqlConnection con, string dest_dbname)
        {
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

        /// <summary>Get default MDF and LDF directories</summary>
        /// <param name="con"></param>
        /// <returns>TRUE=success, FALSE=error</returns>
        private static bool GetDefaultDirs(SqlConnection con)
        {
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

            return ret;
        }

        /// <summary>Check if the specified database needs to have a transaction log backup</summary>
        /// <param name="con"></param>
        /// <param name="src_dbname"></param>
        /// <returns>1=yes, it has a transaction log, 0=no, there's no transaction log, -1=error</returns>
        private static int HasTLog(SqlConnection con, string src_dbname)
        {
            int ret = 0;    // Assume simple recovery model

            try
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = string.Format("SELECT recovery_model_desc FROM master.sys.databases WHERE name='{0}'",src_dbname);
                    object value=cmd.ExecuteScalar();
                    switch (value.ToString().ToUpper())
                    {
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

            return ret;
        } // HasTLog

        /// <summary>Execute the given SQL, with the expectation that it doesn't return a result set</summary>
        /// <param name="con">SQL instance</param>
        /// <param name="sql">The SQL to execute</param>
        /// <param name="type">Specify either SQL command (default), or stored procedure</param>
        /// <returns>Success: TRUE, errors: FALSE</returns>
        private static bool RunSQL(SqlConnection con, string sql, System.Data.CommandType type=System.Data.CommandType.Text)
        {
            bool ret = true;

            try
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandType = type;
                    cmd.CommandTimeout = 600;   // 10 minutes
                    cmd.ExecuteNonQuery();
                } // using: SqlCommand
            }
            catch(Exception ex)
            {
                Message(string.Format("Error running {0}\n{1}", sql, ex.ToString()));
                ret = false;
            }

            return ret;
        } // RunSQL

        /// <summary>Copies the database</summary>
        /// <param name="src_con"></param>
        /// <param name="src_dbname"></param>
        /// <param name="dest_con"></param>
        /// <param name="dest_dbname"></param>
        private static void CopyDatabase(SqlConnection src_con, string src_dbname, SqlConnection dest_con, string dest_dbname)
        {
            bool ok = true;

            // Make a copy-only backup of the source database
            Message("Get copy of source DB (MDF)");
            if (!RunSQL(src_con, string.Format(@"BACKUP DATABASE [{0}] TO DISK = N'{2}\{0}.bak' WITH COPY_ONLY, NOFORMAT, NOINIT, NAME = N'{0}-Database copy to {1}', SKIP, NOREWIND, NOUNLOAD,  STATS = 10", src_dbname, dest_instance,holdingpath)))
                ok = false;
            if (ok && (backuplog==1))
                Message("Get copy of source DB (LDF)");
                if (!RunSQL(src_con, string.Format(@"BACKUP LOG [{0}] TO  DISK = N'{2}\{0}.bak' WITH  COPY_ONLY, NOFORMAT, NOINIT,  NAME = N'{0}-Database copy to {1}', SKIP, NOREWIND, NOUNLOAD,  STATS = 10", src_dbname, dest_instance,holdingpath)))
                    ok = false;

            if (ok)
            {
                string tmp = GetLogicalNames(src_con, string.Format(@"{0}\{1}.bak",holdingpath, src_dbname));
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
                    SqlCommand cmd = dest_con.CreateCommand();
                    cmd.CommandText = string.Format(@"RESTORE DATABASE[{0}] FROM DISK = N'{6}\{3}.bak' WITH FILE = 1, MOVE N'{4}' TO N'{1}{0}.mdf', MOVE N'{5}' TO N'{2}{0}_log.ldf', NORECOVERY,  NOUNLOAD,  REPLACE,  STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname,logicalname_d,logicalname_l,holdingpath);
                    cmd.ExecuteNonQuery();

                    if (backuplog == 1)
                    {
                        Message("\tTransaction log");
                        cmd.CommandText = string.Format(@"RESTORE LOG [{0}] FROM  DISK = N'{4}\{3}.bak' WITH  FILE = 1, NOUNLOAD,  RECOVERY, STATS = 5", dest_dbname, MDFdir, LDFdir, src_dbname, holdingpath);
                        cmd.ExecuteNonQuery();
                    } // if: has transaction log?

                    Message("\tSet multiuser");
                    cmd.CommandText = string.Format(@"ALTER DATABASE[{0}] SET MULTI_USER", dest_dbname);
                    cmd.ExecuteNonQuery();

                    // Clean up after ourselves
                    System.IO.File.Delete(string.Format(@"{0}\{1}.bak",holdingpath, src_dbname));

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
