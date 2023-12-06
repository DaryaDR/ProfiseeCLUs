using CommandLine;
using Microsoft.Extensions.DependencyModel;
using NLog;
using Profisee.MasterDataMaestro.Services.Contracts.DataContracts.Common.Expression;
using Profisee.MasterDataMaestro.Services.DataContracts;
using Profisee.MasterDataMaestro.Services.DataContracts.MasterDataServices;
using Profisee.MasterDataMaestro.Services.MessageContracts;
using Profisee.Services.Sdk.AcceleratorFramework;
using Profisee.Services.Sdk.Common.Contracts.DataContext;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;

namespace ProfiseeMatch
{
    class Program
    {
        public class Options
        {
            [Option('s', "strategy", Required = true, HelpText = "Matching Strategy Name")]
            public string MatchingStrategyName { get; set; }

            [Option('c', "clientid", Required = true, HelpText = "ClientId for the matching strategy to process under")]
            public string ClientId { get; set; }

            [Option('u', "uri", Required = true, HelpText = "The Profisee URI")]
            public string URI { get; set; }

            [Option('v', "verifyNextMatchGroupSequence", Default = false, Required = false, HelpText = "Veryfy next match group sequence when matching")]
            public bool VerifyNextMatchGroupSequence { get; set; }

            [Option('r', "reset", Default = false, Required = false, HelpText = "Unmatch members")]
            public bool Reset { get; set; }

            [Option('b', "both", Default = false, Required = false, HelpText = "Both Unmatch members and then match")]
            public bool Both { get; set; }

            [Option('x', "indexupdateonly", Default = false, Required = false, HelpText = "Index Update Only")]
            public bool IndexUpdateOnly { get; set; }

            [Option('i', "IndexUpdate", Default = false, Required = false, HelpText = "Index Update")]
            public bool IndexUpdate { get; set; }
        }

        private const int DefaultMatchingBatchSize = 250;
        private static bool ReturnError = false;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public const string AssemblyVersion = "2.0.0.0";
        public const string AssemblyFileVersion = "2.0.0.0";

        static int Main(string[] args) {
            // Need to use Logger before the AssemblyResolver is attached otherwise an infinite loop...
            Logger.Info($"---Starting ProfiseeMatch v{AssemblyFileVersion}---");
            Logger.Info($"Using SDKLocation=>'{SDKLocation}'");

            AssemblyLoadContext.Default.Resolving += (context, name) => {
                // avoid loading *.resources dlls, because of: https://github.com/dotnet/coreclr/issues/8416
                if (name.Name.EndsWith("resources")) return null;

                var foundDlls = Directory.GetFileSystemEntries(new FileInfo(SDKLocation).FullName, name.Name + ".dll", System.IO.SearchOption.AllDirectories);
                if (foundDlls.Any()) return context.LoadFromAssemblyPath(foundDlls[0]);
                return context.LoadFromAssemblyName(name);
            };

            try {
                Parser.Default.ParseArguments<Options>(args).WithParsed(options => {               
                    Logger.Info("---Logging into MDM Server---");

                    var mdmSrc = new MdmSource();
                    mdmSrc.Connect(options.URI, options.ClientId);

                    Logger.Info("---Logged into MDM Server---");

                    var model = mdmSrc.GetModel();
                    if (model == null) { throw new Exception("Could not retrieve model from server"); }

                    var strategy = model.GetMatchingStrategy(options.MatchingStrategyName);
                    if (strategy == null) { throw new Exception("Could not retrieve strategy from model"); }

                    if (options.IndexUpdate || options.IndexUpdateOnly)
                        IndexUpdate(model, strategy);

                    if (!options.IndexUpdateOnly) {
                        if (options.Both) {
                            UnmatchMembers(model, strategy);

                            Collection<string> matchGroups = MatchMembers(model, strategy, options.VerifyNextMatchGroupSequence);
                            SurviveMatchGroups(model, strategy, matchGroups);
                        } else {
                            if (options.Reset) {
                                UnmatchMembers(model, strategy);
                            } else {
                                Collection<string> matchGroups = MatchMembers(model, strategy, options.VerifyNextMatchGroupSequence);
                                SurviveMatchGroups(model, strategy, matchGroups);
                            }
                        }
                    }

                    Logger.Info($"---Exiting ProfiseeMatch v{AssemblyFileVersion}---");
                });
            } catch (Exception exception) {
                Logger.Error($@"Exception: {exception.Message}
{exception.StackTrace}");
                ReturnError = true;
            }
            if (Environment.MachineName.Equals("CORPLTR18")) {
                Console.WriteLine($"On Profisee Laptop - Press Any Key to Close Console...");
                Console.ReadLine();
            }
            return ReturnError ? 0 : 1;
        }

        private static BrowseEntityContext GetBrowseEntityContext(int pageSize, string processFlag) {
            var browseEntityContext = new BrowseEntityContext() { IdentityOnly = true, PageSize = pageSize };
            browseEntityContext.FilterExpression = Filter.On(processFlag, BinaryOperator.Equals, TrueValue).FilterExpression;
            return browseEntityContext;
        }
        public static int GetSettingAsInt(string settingName, int defaultValue) {
            var value = ConfigurationManager.AppSettings[settingName];
            if (value != null)
                if (int.TryParse(value, out var valueAsInt))
                    return valueAsInt;
            return defaultValue;
        }
        public static string GetSettingAsString(string settingName, string defaultValue) {
            var value = ConfigurationManager.AppSettings[settingName];
            if (value != null)
                return value;
            return defaultValue;
        }
        public static bool GetSettingAsBoolean(string settingName, bool defaultValue) {
            var value = ConfigurationManager.AppSettings[settingName];
            if (!string.IsNullOrEmpty(value))
                return value.Equals("true", StringComparison.InvariantCultureIgnoreCase) ? true : false;
            return defaultValue;
        }
        private static int MatchingBatchSize => GetSettingAsInt("MatchingBatchSize", 250);
        private static string MatchProcessFlag => GetSettingAsString("MatchProcessFlag", "BatchProcessFlag");
        private static string UnmatchProcessFlag => GetSettingAsString("UnmatchProcessFlag", "UnmatchFlag");
        private static string TrueValue => GetSettingAsString("TrueValue", "True");
        private static string DatabaseConnectionString => GetSettingAsString("DatabaseConnectionString", string.Empty);
        private static string UnmatchStoredProcedureName => GetSettingAsString("UnmatchStoredProcedureName", string.Empty);
        private static string MatchStoredProcedureName => GetSettingAsString("MatchStoredProcedureName", string.Empty);
        private static string MatchClearAttributes => GetSettingAsString("MatchClearAttributes", string.Empty);
        private static string UnmatchClearAttributes => GetSettingAsString("UnmatchClearAttributes", string.Empty);
        //private static string UpdateIndexAttribute => GetSettingAsString("UpdateIndexAttribute", string.Empty);
        private static bool Verbose => GetSettingAsBoolean("Version", false);

        private static int StrategyStatusPollingInterval = GetSettingAsInt("StrategyStatusPollingInterval", 2500);

        private static string SDKLocation = GetSettingAsString("SDKLocation", "C:\\Program Files\\Profisee\\Profisee SDK\\23.2.0 R1");

        public static void IndexUpdate(MdmModel model, MatchingStrategy strategy) {
            Logger.Debug($"IndexUpdate Start");

            try {
                Logger.Debug($"Processing Matching Strategy 'IndexAddOrUpdate'.");

                var processMatchingStrategyResult = model.ProcessMatchingStrategy(new ProcessMatchingStrategyRequest {
                    StrategyId = strategy.Identifier,
                    Action = ProcessAction.IndexAddOrUpdate | ProcessAction.RunMatchingIndexCreation
                });

                if (processMatchingStrategyResult.OperationResult.Errors.Count > 0) {
                    foreach (var error in processMatchingStrategyResult.OperationResult.Errors) {
                        Logger.Error($"Error encountered while processing Matching StrategyError message: {error.Code} - {error.Context} - {error.Description}");
                        ReturnError = true;
                        return;
                    }
                }

                var processStatus = model.GetMatchingStrategyStatus(strategy.Identifier);
                Logger.Debug($"Starting ProcessingStatus = '{processStatus}'");
                Thread.Sleep(StrategyStatusPollingInterval); // Initial Wait to make sure we start
                Logger.Debug($"Delayed ProcessingStatus = '{processStatus}'");

                Logger.Debug($"Now wait for strategy to complete.");

                do {
                    Thread.Sleep(StrategyStatusPollingInterval);

                    processStatus = model.GetMatchingStrategyStatus(strategy.Identifier);
                    Logger.Info($"   ProcessingStatus = '{processStatus}'");
                } while (processStatus != ProcessingStatus.Error && processStatus != ProcessingStatus.Finished && processStatus != ProcessingStatus.Canceled);

                Logger.Info($"Processing Matching Strategy 'IndexAddOrUpdate' complete.");
            } catch (Exception exception) {
                Logger.Error($@"Exception: {exception.Message}
{exception.StackTrace}");
            }
            Logger.Debug($"IndexUpdate End");
        }
        private static void UnmatchMembers(MdmModel model, MatchingStrategy strategy) {
            Logger.Info("---Executing Unmatching---");

            const int UnmatchMembersMaxRecordsProcessedCount = 50;
            var browseEntityContext = GetBrowseEntityContext(Math.Min(MatchingBatchSize, UnmatchMembersMaxRecordsProcessedCount), UnmatchProcessFlag);
            browseEntityContext.IdentityOnly = false;

            var entity = model.GetEntity(strategy.EntityId);
            var entityMembersInfo = entity.GetInformation("VERSION_1", browseEntityContext);

            if (entityMembersInfo.TotalMemberCount <= 0) {
                Logger.Info("No records to process.");
                return;
            }

            Logger.Debug($"Processing for: {entityMembersInfo.TotalMemberCount} records");
            Logger.Debug($"Page Size: {browseEntityContext.PageSize}");
            Logger.Debug($"Total Pages: {entityMembersInfo.TotalPages}");

            var unmatchedCodes = new HashSet<string>();
            var unmatchedMasterCodes = new HashSet<string>();

            for (browseEntityContext.PageNumber = 1; browseEntityContext.PageNumber <= entityMembersInfo.TotalPages; browseEntityContext.PageNumber++) {
                Logger.Debug($"Getting page {browseEntityContext.PageNumber} records");

                var members = entity.GetMdmMembers(browseEntityContext, new Collection<string> { "Master" });

                // Now add the member code to the list to pass to the stored procedure
                // may need to move this so that it is only populated when there is not error during unmatching
                // Error handling needs some work here...
                foreach (var member in members) {
                    unmatchedCodes.Add(member.MemberId.Code);

                    var masterMemberCode = member.Member.GetAttributeValueAsString("Master");
                    if (!string.IsNullOrEmpty(masterMemberCode))
                        unmatchedMasterCodes.Add(masterMemberCode);
                }

                Logger.Debug($"Unmatching page {browseEntityContext.PageNumber} records");

                var unmatchMembersResponseErrors = model.UnmatchMembers(strategy.Identifier, new Collection<MemberIdentifier>(members.Select(x => x.MemberId).ToList()));

                Logger.Debug($"Unmatching page {browseEntityContext.PageNumber} records complete");

                if (unmatchMembersResponseErrors.Count > 0) {
                    Logger.Error($"Error encountered while unmatching page {browseEntityContext.PageNumber} records.");
                    foreach (var error in unmatchMembersResponseErrors) {
                        Logger.Error($"Error message: {error.Code} - {error.Context} - {error.Description}");
                        ReturnError = true;
                    }
                }

                if (!string.IsNullOrEmpty(UnmatchClearAttributes)) {
                    Logger.Debug($"Clearing '{UnmatchClearAttributes}' attributes.");

                    var membersToClearAttributes = new Collection<MdmMember>();
                    foreach (var member in members) {
                        var memberToClearAttributes = entity.GetNewMdmMemberTemplate();
                        memberToClearAttributes.MemberId.Code = member.Code;
                        foreach (var attributeName in UnmatchClearAttributes.Split(','))
                            memberToClearAttributes.SetMemberValue(attributeName, null, true);
                        membersToClearAttributes.Add(memberToClearAttributes);
                    }

                    var errors = entity.UpdateMdmMembers(membersToClearAttributes);
                    foreach (var error in errors)
                        Logger.Error($"Error message: {error.Code} - {error.Context} - {error.Description}");

                    Logger.Debug($"Done clearing '{UnmatchClearAttributes}' attributes.");
                }
            }

            // Now call the stored procedure with the Codes that we unmatched...
            if (!string.IsNullOrEmpty(DatabaseConnectionString) && !string.IsNullOrEmpty(UnmatchStoredProcedureName)) {
                Logger.Debug($"Calling '{UnmatchStoredProcedureName}'.");
                try {
                    using (var sqlConnection = new SqlConnection(DatabaseConnectionString)) {
                        sqlConnection.Open();

                        using (var sqlCommand = sqlConnection.CreateCommand()) {
                            sqlCommand.CommandType = CommandType.StoredProcedure;
                            sqlCommand.CommandText = UnmatchStoredProcedureName;

                            Logger.Debug($"@MemberCodes = ({string.Join(",", unmatchedCodes)})");
                            var memberCodesParameter = sqlCommand.Parameters.AddWithValue("@MemberCodes", CreateTableFromList(unmatchedCodes.ToList()));
                            memberCodesParameter.SqlDbType = SqlDbType.Structured;
                            memberCodesParameter.TypeName = "dbo.MemberCodeTableType";

                            Logger.Debug($"@MasterMemberCodes = ({string.Join(",", unmatchedMasterCodes)})");
                            var masterMemberCodesParameter = sqlCommand.Parameters.AddWithValue("@MasterMemberCodes", CreateTableFromList(unmatchedMasterCodes.ToList()));
                            masterMemberCodesParameter.SqlDbType = SqlDbType.Structured;
                            masterMemberCodesParameter.TypeName = "dbo.MemberCodeTableType";

                            sqlCommand.ExecuteNonQuery();
                        }
                    }
                } catch (Exception exception) {
                    Logger.Error($@"Exception: {exception.Message}
{exception.StackTrace}");
                }
                Logger.Debug($"Done calling '{UnmatchStoredProcedureName}'.");
            }
            return;
        }

        private static DataTable CreateTableFromList(List<string> memberCodes) {
            var memberCodesDataTable = new DataTable();
            memberCodesDataTable.Columns.Add(new DataColumn("Code", typeof(string)));
            foreach (var memberCode in memberCodes) {
                var dataRow = memberCodesDataTable.NewRow();
                dataRow[0] = memberCode;
                memberCodesDataTable.Rows.Add(dataRow);
            }
            return memberCodesDataTable;
        }

        private static Collection<string> MatchMembers(MdmModel model, MatchingStrategy strategy, bool verifyNextMatchGroupSequence) {
            Logger.Info("---Executing Matching---");

            var batchSizeStr = ConfigurationManager.AppSettings["MatchingBatchSize"];

            if (!int.TryParse(batchSizeStr, out var batchSize)) {
                Logger.Warn($"Could not parse MatchingBatchSize value as integer: {batchSizeStr}");
                batchSize = DefaultMatchingBatchSize;
            }

            var browseEntityContext = GetBrowseEntityContext(batchSize, MatchProcessFlag);
            var entity = model.GetEntity(strategy.EntityId);
            var entityMembersInfo = entity.GetInformation("VERSION_1", browseEntityContext);

            if (entityMembersInfo.TotalMemberCount <= 0) {
                Logger.Info("No records to process.");
                return new Collection<string>();
            }

            Logger.Debug($"Processing for: {entityMembersInfo.TotalMemberCount} records");
            Logger.Debug($"Page Size: {browseEntityContext.PageSize}");
            Logger.Debug($"Total Pages: {entityMembersInfo.TotalPages}");

            var allMatchGroups = new HashSet<string>(); // a hashset is used for implicit deduplication
            var nonUniqueMatchGroups = new HashSet<string>(); // a hashset is used for implicit deduplication
            var memberCodes = new HashSet<string>();

            for (browseEntityContext.PageNumber = 1; browseEntityContext.PageNumber <= entityMembersInfo.TotalPages; browseEntityContext.PageNumber++) {
                Logger.Debug($"Getting page {browseEntityContext.PageNumber} records");

                var members = entity.GetMdmMembers(browseEntityContext);

                Logger.Debug($"Matching page {browseEntityContext.PageNumber} records");

                foreach (var member in members) {
                    // Because is missing...
                    if (Verbose) Logger.Debug($"MatchMember('{member.Code}') Starting...");

                    var matchMemberRequest = new MatchMemberRequest() { MemberId = member.MemberId, StrategyId = strategy.Identifier, VerifyNextMatchGroupSequence = verifyNextMatchGroupSequence };
                    var matchMemberResponse = model.MatchMember(matchMemberRequest);

                    if (Verbose) Logger.Debug($"MatchMember('{member.Code}') Ending...");

                    if (matchMemberResponse.OperationResult.Errors.Count > 0) {
                        foreach (var error in matchMemberResponse.OperationResult.Errors) {
                            Logger.Error($"Error encountered while matching record {member.Code}. Error message: {error.Code} - {error.Context} - {error.Description}");
                            ReturnError = true;
                        }
                    }

                    if (Verbose) Logger.Debug($"MatchMember('{member.Code}').Result.Status = '{matchMemberResponse.Result.Status.ToString()}'.");
                    if (Verbose) Logger.Debug($"matchMemberResponse.Result.MatchGroup = '{matchMemberResponse.Result.MatchGroup}'");
                    var matchGroup = matchMemberResponse.Result.MatchGroup;

                    if (!string.IsNullOrEmpty(matchGroup)) {
                        if (matchMemberResponse.Result.Status != MatchStatus.Unique)
                            nonUniqueMatchGroups.Add(matchGroup);
                        allMatchGroups.Add(matchGroup);
                    } else
                        Logger.Debug($"Skipping because MatchGroup is empty or null.");

                    memberCodes.Add(member.Code);
                }

                if (!string.IsNullOrEmpty(MatchClearAttributes)) {
                    Logger.Debug($"Clearing '{MatchClearAttributes}' attributes.");

                    var membersToClearAttributes = new Collection<MdmMember>();
                    foreach (var member in members) {
                        var memberToClearAttributes = entity.GetNewMdmMemberTemplate();
                        memberToClearAttributes.MemberId.Code = member.Code;
                        foreach (var attributeName in MatchClearAttributes.Split(','))
                            memberToClearAttributes.SetMemberValue(attributeName, null, true);
                        membersToClearAttributes.Add(memberToClearAttributes);
                    }

                    Logger.Debug($"UpdateMdmMembers Start.");
                    var errors = entity.UpdateMdmMembers(membersToClearAttributes);
                    Logger.Debug($"UpdateMdmMembers End.");
                    foreach (var error in errors)
                        Logger.Error($"Error message: {error.Code} - {error.Context} - {error.Description}");

                    Logger.Debug($"Done clearing '{MatchClearAttributes}' attributes.");
                }
            }

            // Now call the stored procedure with the Codes that we matched...
            if (!string.IsNullOrEmpty(DatabaseConnectionString) && !string.IsNullOrEmpty(MatchStoredProcedureName)) {
                Logger.Debug($"Calling '{MatchStoredProcedureName}' Start.");
                try {
                    using (var sqlConnection = new SqlConnection(DatabaseConnectionString)) {
                        sqlConnection.Open();

                        using (var sqlCommand = sqlConnection.CreateCommand()) {
                            sqlCommand.CommandType = CommandType.StoredProcedure;
                            sqlCommand.CommandText = MatchStoredProcedureName;

                            Logger.Debug("@MemberCodes = ({codes})", memberCodes);
                            var memberCodesParameter = sqlCommand.Parameters.AddWithValue("@MemberCodes", CreateTableFromList(memberCodes.ToList()));
                            memberCodesParameter.SqlDbType = SqlDbType.Structured;
                            memberCodesParameter.TypeName = "dbo.MemberCodeTableType";

                            Logger.Debug("@AllMatchGroups = ({allGroups})", allMatchGroups);
                            var allMasterMemberCodesParameter = sqlCommand.Parameters.AddWithValue("@AllMatchGroups", CreateTableFromList(allMatchGroups.ToList()));
                            allMasterMemberCodesParameter.SqlDbType = SqlDbType.Structured;
                            allMasterMemberCodesParameter.TypeName = "dbo.MemberCodeTableType";

                            Logger.Debug("@NonUniqueMatchGroups = ({nonUniqueGroups})", nonUniqueMatchGroups);
                            var nonUniqueMasterMemberCodesParameter = sqlCommand.Parameters.AddWithValue("@NonUniqueMatchGroups", CreateTableFromList(nonUniqueMatchGroups.ToList()));
                            nonUniqueMasterMemberCodesParameter.SqlDbType = SqlDbType.Structured;
                            nonUniqueMasterMemberCodesParameter.TypeName = "dbo.MemberCodeTableType";

                            sqlCommand.ExecuteNonQuery();
                        }
                    }
                    Logger.Debug($"Calling '{MatchStoredProcedureName}' End.");
                } catch (Exception exception) {
                    Logger.Error($@"Exception: {exception.Message}
{exception.StackTrace}");
                }
                Logger.Debug($"Done calling '{MatchStoredProcedureName}'.");
            }

            var returnVal = new Collection<string>();

            foreach (var matchGroup in allMatchGroups) {
                returnVal.Add(matchGroup);
            }

            return returnVal;
        }

        private static void SurviveMatchGroups(MdmModel model, MatchingStrategy strategy, Collection<string> matchGroups) {
            Logger.Info("---Executing Surviorship---");
            Logger.Info($"matchGroups.Count = {matchGroups.Count}");

            if (matchGroups.Count <= 0) {
                Logger.Info("No match groups to survive.");
                return;
            }

            Logger.Debug($"Surviving {matchGroups.Count} match groups");

            var surviveMatchGroupsRequest = new SurviveMatchGroupsRequest() { MatchGroups = matchGroups, StrategyId = strategy.Identifier };
            var surviveMatchGroupsResponse = model.SurviveMatchGroups(surviveMatchGroupsRequest);

            Logger.Debug($"Surviving {matchGroups.Count} match groups complete");

            if (surviveMatchGroupsResponse.OperationResult.Errors.Count > 0) {
                foreach (var error in surviveMatchGroupsResponse.OperationResult.Errors) {
                    Logger.Error($"Error encountered while running survivorship. Error message: {error.Code} - {error.Context} - {error.Description}");
                    ReturnError = true;
                }
            }
        }
    }

    public static class MemberHelpers
    {
        public static object GetAttributeValue(this Member member, string attributeName) {
            if (attributeName.Equals("Name")) return member.MemberId.Name;
            if (attributeName.Equals("Code")) return member.MemberId.Code;

            var attribute = member.Attributes.FirstOrDefault(a => a.Name.Equals(attributeName, StringComparison.InvariantCultureIgnoreCase));
            if (attribute != null) return attribute.Value;
            return null;
        }
        public static string GetAttributeValueAsString(this Member member, string attributeName) {
            var attributeValue = member.GetAttributeValue(attributeName);
            if (attributeValue != null) {
                if (attributeValue is MemberIdentifier) return ((MemberIdentifier)attributeValue).Code;
                return attributeValue.ToString();
            }
            return string.Empty;
        }
    }
}
