// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaSealValidatorTests
    {
        private AuRaSealValidator _sealValidator;
        private AuRaChainSpecEngineParameters _auRaParameters;
        private IAuRaStepCalculator _auRaStepCalculator;
        private ILogManager _logManager;
        private IWallet _wallet;
        private Address _address;
        private IEthereumEcdsa _ethereumEcdsa;
        private static int _currentStep;
        private IReportingValidator _reportingValidator;
        private IBlockTree _blockTree;
        private IValidSealerStrategy _validSealerStrategy;

        [SetUp]
        public void SetUp()
        {
            _auRaParameters = new AuRaChainSpecEngineParameters();
            _auRaStepCalculator = Substitute.For<IAuRaStepCalculator>();
            _logManager = LimboLogs.Instance;
            _wallet = new DevWallet(new WalletConfig(), _logManager);
            _address = _wallet.NewAccount(new NetworkCredential(string.Empty, "AAA").SecurePassword);

            _ethereumEcdsa = Substitute.For<IEthereumEcdsa>();
            _currentStep = 11;
            _auRaStepCalculator.CurrentStep.Returns(_currentStep);

            _reportingValidator = Substitute.For<IReportingValidator>();
            _blockTree = Substitute.For<IBlockTree>();
            _validSealerStrategy = Substitute.For<IValidSealerStrategy>();
            _sealValidator = new AuRaSealValidator(_auRaParameters,
                _auRaStepCalculator,
                _blockTree,
                Substitute.For<IValidatorStore>(),
                _validSealerStrategy,
                _ethereumEcdsa,
                new Lazy<IReportingValidator>(_reportingValidator),
                _logManager);
        }

        public enum Repeat
        {
            No,
            Yes,
            YesChangeHash
        }

        private static IEnumerable ValidateParamsTests
        {
            get
            {
                long step = 10;
                long parentStep = 9;

                BlockHeaderBuilder GetBlock() => Build.A.BlockHeader
                        .WithAura(10, [])
                        .WithBeneficiary(TestItem.AddressA)
                        .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(parentStep, step));

                BlockHeaderBuilder GetParentBlock() => Build.A.BlockHeader
                    .WithAura(parentStep, [])
                    .WithBeneficiary(TestItem.AddressB);

                TestCaseData GetTestCaseData(
                    BlockHeaderBuilder parent,
                    BlockHeaderBuilder block,
                    Action<AuRaChainSpecEngineParameters> paramAction = null,
                    Repeat repeat = Repeat.No,
                    bool parentIsHead = true,
                    bool isValidSealer = true) =>
                    new(parent.TestObject, block.TestObject, paramAction, repeat, parentIsHead, isValidSealer);


                yield return GetTestCaseData(GetParentBlock(), GetBlock())
                    .Returns((true, (object)null)).SetName("General valid.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(step, null))
                    .Returns((false, (object)null)).SetName("Missing AuRaSignature").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(parentStep, []))
                    .Returns((false, IReportingValidator.MaliciousCause.DuplicateStep)).SetName("Duplicate block.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(parentStep - 1, []))
                    .Returns((false, IReportingValidator.MaliciousCause.DuplicateStep)).SetName("Past block.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(parentStep + 7, []))
                    .Returns((false, IReportingValidator.BenignCause.FutureBlock)).SetName("Future block.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(parentStep + 3, []))
                    .Returns((false, IReportingValidator.BenignCause.SkippedStep)).SetName("Skipped steps.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithDifficulty(AuraDifficultyCalculator.MaxDifficulty))
                    .Returns((false, (object)null)).SetName("Difficulty too large.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithDifficulty(1000))
                    .Returns((false, (object)null)).SetName("Wrong difficulty.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithDifficulty(1000), a => a.ValidateScoreTransition = 100)
                    .Returns((true, (object)null)).SetName("Skip difficulty validation due to ValidateScoreTransition.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(parentStep - 1, []), a => a.ValidateScoreTransition = a.ValidateStepTransition = 100)
                    .Returns((true, (object)null)).SetName("Skip step validation due to ValidateStepTransition.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(), repeat: Repeat.Yes)
                    .Returns((true, (object)null)).SetName("Same block twice.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(), repeat: Repeat.YesChangeHash)
                    .Returns((true, IReportingValidator.MaliciousCause.SiblingBlocksInSameStep)).SetName("Sibling in same step.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(), parentIsHead: false, isValidSealer: false)
                    .Returns((true, (object)null)).SetName("Cannot validate sealer").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(), parentIsHead: true, isValidSealer: false)
                    .Returns((false, (object)null)).SetName("Wrong sealer").SetCategory("ValidParams");
            }
        }

        [TestCaseSource(nameof(ValidateParamsTests))]
        public (bool, object) validate_params(BlockHeader parentBlock, BlockHeader block, Action<AuRaChainSpecEngineParameters> modifyParameters, Repeat repeat, bool parentIsHead, bool isValidSealer)
        {
            _blockTree.Head.Returns(parentIsHead ? new Block(parentBlock) : new Block(Build.A.BlockHeader.WithNumber(parentBlock.Number - 1).TestObject));
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), block.Beneficiary, block.AuRaStep.Value, out _).Returns(isValidSealer);

            object cause = null;

            _reportingValidator.ReportBenign(Arg.Any<Address>(), Arg.Any<long>(), Arg.Do<IReportingValidator.BenignCause>(c => cause ??= c));
            _reportingValidator.ReportMalicious(Arg.Any<Address>(), Arg.Any<long>(), Arg.Any<byte[]>(), Arg.Do<IReportingValidator.MaliciousCause>(c => cause ??= c));
            BlockHeader header = null, parent = null;
            _reportingValidator.TryReportSkipped(Arg.Do<BlockHeader>(h => header = h), Arg.Do<BlockHeader>(h => parent = h));

            modifyParameters?.Invoke(_auRaParameters);
            bool validateParams = _sealValidator.ValidateParams(parentBlock, block);

            if (header?.AuRaStep > parent?.AuRaStep + 1)
            {
                _reportingValidator.ReportBenign(header.Beneficiary, header.Number, IReportingValidator.BenignCause.SkippedStep);
            }

            if (repeat != Repeat.No)
            {
                if (repeat == Repeat.YesChangeHash)
                {
                    block.Hash = Keccak.Compute("AAA");
                }

                validateParams = _sealValidator.ValidateParams(parentBlock, block);
            }

            return (validateParams, cause);
        }

        private static IEnumerable ValidateSealTests
        {
            get
            {
                yield return new TestCaseData(0, null, TestItem.AddressA).Returns(true).SetName("Genesis valid.").SetCategory("ValidSeal");
                yield return new TestCaseData(1, null, TestItem.AddressA).Returns(false).SetName("Wrong sealer.").SetCategory("ValidSeal");
                yield return new TestCaseData(1, null, null).Returns(true).SetName("General valid.").SetCategory("ValidSeal");
            }
        }

        [TestCaseSource(nameof(ValidateSealTests))]
        public bool validate_seal(long blockNumber, Address signedAddress, Address recoveredAddress)
        {
            signedAddress ??= _address;
            recoveredAddress ??= _address;

            BlockHeader block = Build.A.BlockHeader
                .WithAura(10, [])
                .WithBeneficiary(_address)
                .WithNumber(blockNumber)
                .TestObject;

            Hash256 hash = block.CalculateHash(RlpBehaviors.ForSealing);
            block.AuRaSignature = _wallet.Sign(hash, signedAddress).BytesWithRecovery;
            _ethereumEcdsa.RecoverAddress(Arg.Any<Signature>(), in hash.ValueHash256).Returns(recoveredAddress);

            return _sealValidator.ValidateSeal(block, false);
        }
    }
}
