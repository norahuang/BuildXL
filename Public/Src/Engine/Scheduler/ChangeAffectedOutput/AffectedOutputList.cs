using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Artifacts;
using BuildXL.Storage;
using BuildXL.Storage.ChangeTracking;
using BuildXL.Storage.InputChange;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;

namespace BuildXL.Scheduler.ChangeAffectedOutput
{
    /// <summary>
    /// Class representing source change affected output list.
    /// </summary>
    public sealed class AffectedOutputList
    {

        /// <summary>
        /// Output file contents which affected by the source change of this build. 
        /// </summary>
        /// <remarks>
        /// This list should be initialized by the input change list provided from the InputChanges configuration option.
        /// </remarks>
        private readonly ConcurrentBigSet<AbsolutePath> m_sourceChangeAffectedOutputFiles = new ConcurrentBigSet<AbsolutePath>();

        private PathTable m_pathTable;

        private FileContentManager m_fileContentManager;

        /// <nodoc />
        public AffectedOutputList(PathTable pathTable, FileContentManager fileContentManager)
        {
            m_pathTable = pathTable;
            m_fileContentManager = fileContentManager;
        }

        /// <summary>
        /// Initial affected output list with the external source change list
        /// </summary>
        public bool InitialAffectedOutputList(InputChangeList inputChangeList, PathTable pathTable)
        {
            var allInputsAreAffected = false;
            // The Algorithm can't deal with removal currently, so disable change-based code coverage and have to run full code coverage if has removal. 
            if (!inputChangeList.ChangedPaths.Any(p => p.PathChanges == PathChanges.Removed))
            {
                foreach (var changePath in inputChangeList.ChangedPaths)
                {
                    switch (changePath.PathChanges)
                    {
                        case PathChanges.DataOrMetadataChanged:
                        case PathChanges.NewlyPresentAsFile:
                            m_sourceChangeAffectedOutputFiles.GetOrAdd(AbsolutePath.Create(pathTable, changePath.Path));
                            break;
                    }

                }
            }
            else
            {
                allInputsAreAffected = true;
            }

            return allInputsAreAffected;
        }

        /// <summary>
        /// Compute the intersection of the pip's dependencies and global affected outputs of the build
        /// </summary>
        public IReadOnlyCollection<AbsolutePath> GetChangeAffectedInputs(Process process, bool allInputsAffected = false)
        {
            var changeAffectedInputs = new HashSet<AbsolutePath>();

            changeAffectedInputs.AddRange(process.Dependencies.Select(o => o.Path).Where(o => m_sourceChangeAffectedOutputFiles.Contains(o)));

            foreach (var directory in process.DirectoryDependencies)
            {
                foreach (var file in m_fileContentManager.ListSealedDirectoryContents(directory))
                {
                    if (allInputsAffected || m_sourceChangeAffectedOutputFiles.Contains(file))
                    {
                        changeAffectedInputs.Add(file.Path);
                    }
                }
            }

            return ReadOnlyArray<AbsolutePath>.FromWithoutCopy(changeAffectedInputs.ToArray());
        }

        /// <summary>
        /// Check if the outputs of this pip are affected by the source change
        /// </summary>
        private bool IsOutputAffectedBySourceChange(
            PathTable pathTable,
            Pip pip,
            ReadOnlyArray<AbsolutePath> dynamicallyObservedFiles)
        {
            switch (pip.PipType)
            {
                case PipType.Process:
                    return IsOutputOfProcessAffectedBySourceChange((Process)pip, pathTable, dynamicallyObservedFiles);
                case PipType.CopyFile:
                    return m_sourceChangeAffectedOutputFiles == null ? false : m_sourceChangeAffectedOutputFiles.Contains(((CopyFile)pip).Source);
                default:
                    return false;
            }
        }

        private bool IsOutputOfProcessAffectedBySourceChange(
            Process process,
            PathTable pathTable,
            ReadOnlyArray<AbsolutePath> dynamicallyObservedFiles)
        {
            // sourceChangeAffectedOutputFiles is initilized with the source change list.
            // If it is null, that means no file change for this build.
            // Thus no output is affected by the change.

            if (m_sourceChangeAffectedOutputFiles.Count == 0)
            {
                return false;
            }

            // Check if any dynamic and static file dependency is sourceChangeAffectedOutputFiles
            var hasAffected = dynamicallyObservedFiles
                .Concat(process.Dependencies.Select(f => f.Path))
                .Any(f => m_sourceChangeAffectedOutputFiles.Contains(f));

            if (hasAffected)
            {
                return true;
            }

            //if (dynamicallyObservedEnumerations.Any() || process.DirectoryDependencies.Any())
            //{
            //    // Check if dynamic and static directory dependencies under any directory of sourceChangeAffectedOutputDirectroies
            //    foreach (var affectedDir in sourceChangeAffectedOutputDirectroies)
            //    {
            //        foreach (var dynamicDir in dynamicallyObservedEnumerations)
            //        {
            //            if (dynamicDir.IsWithin(pathTable, affectedDir) || affectedDir.Path.IsWithin(pathTable, dynamicDir))
            //            {
            //                return true;
            //            }
            //        }

            //        foreach (var staticDir in process.DirectoryDependencies)
            //        {
            //            foreach ( var content in m_fileContentManager.ListSealedDirectoryContents(staticDir))
            //            {
            //                if (content.Path.IsWithin(pathTable, affectedDir))
            //                {
            //                    return true;
            //                }
            //            }
            //        }
            //    }

            //    // check if the dynamic or static directroy dependency contain any file from sourceChangeAffectedOutputFiles
            //    foreach (var affectedFile in sourceChangeAffectedOutputFiles)
            //    {
            //        foreach (var dynamicDir in dynamicallyObservedEnumerations)
            //        {
            //            if (affectedFile.Path.IsWithin(pathTable, dynamicDir))
            //            {
            //                return true;
            //            }
            //        }

            //        foreach (var staticDir in process.DirectoryDependencies)
            //        {
            //            if (m_fileContentManager.ListSealedDirectoryContents(staticDir).Contains(affectedFile))
            //            {
            //                return true;
            //            }
            //        }
            //    }
            //}

            return false;
        }

        /// <inheritdoc />
        public void ReportSourceChangeAffectedOutputs(
            PipResultStatus status,
            Pip pip,
            ReadOnlyArray<AbsolutePath> dynamicallyObservedFiles,
            IReadOnlyCollection<FileArtifact> outputContent = null)
        {
            if (IsOutputAffectedBySourceChange(m_pathTable, pip, dynamicallyObservedFiles))
            {
                if (outputContent != null)
                {
                    foreach (var output in outputContent)
                    {
                        m_sourceChangeAffectedOutputFiles.GetOrAdd(output);
                    }
                }
            }
        }

        public bool IsSourceChangedAffectedFile(AbsolutePath path)
        {
            return m_sourceChangeAffectedOutputFiles.Contains(path);
        }

        public void ReportSourceChangedAffectedFile(AbsolutePath path)
        {
            m_sourceChangeAffectedOutputFiles.Add(path);
        }
    }
}
