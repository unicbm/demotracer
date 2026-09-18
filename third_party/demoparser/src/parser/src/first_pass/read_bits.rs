use bitter::BitReader;
use bitter::LittleEndianReader;
use std::fmt;

pub struct Bitreader<'a> {
    source: &'a [u8],
    pub reader: LittleEndianReader<'a>,
    pub bits_left: u32,
    pub bits: u64,
    pub total_bits_left: u32,
}
pub fn read_varint(bytes: &[u8], ptr: &mut usize) -> Result<u32, DemoParserError> {
    let mut result: u32 = 0;
    let mut count: u8 = 0;
    loop {
        if count >= 5 {
            return Ok(result as u32);
        }
        let b = match bytes.get(*ptr) {
            Some(b) => *b as u32,
            None => return Err(DemoParserError::OutOfBytesError),
        };
        *ptr += 1;
        result |= (b & 127) << (7 * count);
        count += 1;
        if b & 0x80 == 0 {
            break;
        }
    }
    Ok(result as u32)
}

impl<'a> Bitreader<'a> {
    pub fn new(bytes: &'a [u8]) -> Bitreader<'a> {
        let b = Bitreader {
            source: bytes,
            reader: LittleEndianReader::new(bytes),
            bits: 0,
            bits_left: 0,
            total_bits_left: 0,
        };
        b
    }
    #[inline(always)]
    pub fn consume(&mut self, n: u32) {
        self.bits_left -= n;
        self.bits >>= n;
    }
    // Small reads advance only the local lookahead. Synchronize bitter when
    // refilling or reading bytes instead of shifting both buffers per field.
    #[inline(always)]
    fn sync_reader(&mut self) {
        let consumed = self.reader.lookahead_bits() - self.bits_left;
        if consumed == 64 {
            self.reader.consume(32);
            self.reader.consume(32);
        } else {
            self.reader.consume(consumed);
        }
    }
    #[inline(always)]
    pub fn peek(&mut self, n: u32) -> u64 {
        self.bits & ((1 << n) - 1)
    }
    #[inline(always)]
    pub fn refill(&mut self) {
        self.sync_reader();
        self.refill_from_reader();
    }
    #[inline(always)]
    fn refill_from_reader(&mut self) {
        self.reader.refill_lookahead();
        let refilled = self.reader.lookahead_bits();
        if refilled > 0 {
            self.bits = self.reader.peek(refilled);
        }
        self.bits_left = refilled;
    }
    #[inline(always)]
    pub fn bits_remaining(&mut self) -> Option<usize> {
        self.reader.bits_remaining()?.checked_sub((self.reader.lookahead_bits() - self.bits_left) as usize)
    }
    #[inline(always)]
    pub fn read_nbits(&mut self, n: u32) -> Result<u32, DemoParserError> {
        if self.bits_left < n {
            self.refill();
        }
        let b = self.peek(n);
        self.consume(n);
        return Ok(b as u32);
    }
    #[inline(always)]
    pub fn read_u_bit_var(&mut self) -> Result<u32, DemoParserError> {
        let bits = self.read_nbits(6)?;
        match bits & 0b110000 {
            0b10000 => return Ok((bits & 0b1111) | (self.read_nbits(4)? << 4)),
            0b100000 => return Ok((bits & 0b1111) | (self.read_nbits(8)? << 4)),
            0b110000 => return Ok((bits & 0b1111) | (self.read_nbits(28)? << 4)),
            _ => return Ok(bits),
        }
    }
    #[inline(always)]
    pub fn read_varint32(&mut self) -> Result<i32, DemoParserError> {
        let x = self.read_varint()? as i32;
        let mut y = x >> 1;
        if x & 1 != 0 {
            y = !y;
        }
        Ok(y as i32)
    }
    #[inline(always)]
    pub fn read_varint(&mut self) -> Result<u32, DemoParserError> {
        let mut result: u32 = 0;
        let mut count: i32 = 0;
        let mut b: u32;
        loop {
            if count >= 5 {
                return Ok(result);
            }
            b = self.read_nbits(8)?;
            result |= (b & 127) << (7 * count);
            count += 1;
            if b & 0x80 == 0 {
                break;
            }
        }
        Ok(result)
    }
    #[inline(always)]
    pub fn read_varint_u_64(&mut self) -> Result<u64, DemoParserError> {
        let mut result: u64 = 0;
        let mut count: i32 = 0;
        let mut b: u32;
        let mut s = 0;
        loop {
            b = self.read_nbits(8)?;
            if b < 0x80 {
                if count > 9 || count == 9 && b > 1 {
                    return Err(DemoParserError::MalformedMessage);
                }
                return Ok(result | (b as u64) << s);
            }
            result |= ((b as u64) & 127) << s;
            count += 1;
            if b & 0x80 == 0 {
                break;
            }
            s += 7;
        }
        Ok(result)
    }
    #[inline(always)]
    pub fn read_boolean(&mut self) -> Result<bool, DemoParserError> {
        Ok(self.read_nbits(1)? != 0)
    }
    pub fn read_n_bytes(&mut self, n: usize) -> Result<Vec<u8>, DemoParserError> {
        let mut bytes = vec![0_u8; n];
        self.sync_reader();
        match self.reader.read_bytes(&mut bytes) {
            true => {
                self.refill_from_reader();
                Ok(bytes)
            }
            false => Err(DemoParserError::FailedByteRead(
                format!(
                    "Failed to read message/command. bytes left in stream: {}, requested bytes: {}",
                    self.reader.bits_remaining().unwrap_or(0).checked_div(8).unwrap_or(0),
                    n,
                )
                .to_string(),
            )),
        }
    }
    pub fn read_n_bytes_mut(&mut self, n: usize, buf: &mut [u8]) -> Result<(), DemoParserError> {
        if buf.len() < n {
            return Err(DemoParserError::MalformedMessage);
        }
        self.sync_reader();
        match self.reader.read_bytes(&mut buf[..n]) {
            true => {
                self.refill_from_reader();
                Ok(())
            }
            false => Err(DemoParserError::FailedByteRead(
                format!(
                    "Failed to read message/command. bytes left in stream: {}, requested bytes: {}",
                    self.reader.bits_remaining().unwrap_or(0).checked_div(8).unwrap_or(0),
                    n,
                )
                .to_string(),
            )),
        }
    }
    /// Advance over a byte payload without copying it, preserving the current
    /// bit alignment. Packet message payloads do not necessarily start on bytes.
    pub fn skip_n_bytes(&mut self, n: usize) -> Result<(), DemoParserError> {
        let remaining = self.bits_remaining().ok_or(DemoParserError::MalformedMessage)?;
        if n > remaining / 8 {
            return Err(DemoParserError::FailedByteRead(format!(
                "Failed to read message/command. bytes left in stream: {}, requested bytes: {}",
                remaining / 8,
                n,
            )));
        }
        if n == 0 {
            return Ok(());
        }
        let total_bits = self.source.len().checked_mul(8).ok_or(DemoParserError::MalformedMessage)?;
        let consumed = total_bits.checked_sub(remaining).ok_or(DemoParserError::MalformedMessage)?;
        let byte_offset = consumed / 8 + n;
        let bit_offset = (consumed % 8) as u32;
        self.reader = LittleEndianReader::new(&self.source[byte_offset..]);
        self.refill_from_reader();
        if bit_offset != 0 {
            self.consume(bit_offset);
        }
        Ok(())
    }
    pub fn read_ubit_var_fp(&mut self) -> Result<u32, DemoParserError> {
        if self.read_boolean()? {
            return Ok(self.read_nbits(2)?);
        }
        if self.read_boolean()? {
            return Ok(self.read_nbits(4)?);
        }
        if self.read_boolean()? {
            return Ok(self.read_nbits(10)?);
        }
        if self.read_boolean()? {
            return Ok(self.read_nbits(17)?);
        }
        return Ok(self.read_nbits(31)?);
    }
    #[inline(always)]
    pub fn read_bit_coord(&mut self) -> Result<f32, DemoParserError> {
        let mut int_val = 0;
        let mut frac_val = 0;
        let i2 = self.read_boolean()?;
        let f2 = self.read_boolean()?;
        if !i2 && !f2 {
            return Ok(0.0);
        }
        let sign = self.read_boolean()?;
        if i2 {
            int_val = self.read_nbits(14)? + 1;
        }
        if f2 {
            frac_val = self.read_nbits(5)?;
        }
        let resol: f64 = 1.0 / (1 << 5) as f64;
        let result: f32 = (int_val as f64 + (frac_val as f64 * resol) as f64) as f32;
        if sign {
            Ok(-result)
        } else {
            Ok(result)
        }
    }
}

#[derive(Debug, PartialEq, Clone)]
pub enum DemoParserError {
    Cancelled,
    ClassMapperNotFoundFirstPass,
    FieldNoDecoder,
    OutOfBitsError,
    OutOfBytesError,
    FailedByteRead(String),
    UnknownPathOP,
    EntityNotFound,
    ClassNotFound,
    MalformedMessage,
    StringTableNotFound,
    Source1DemoError,
    DemoEndsEarly(String),
    UnknownFile,
    IncorrectMetaDataProp,
    UnknownPropName(String),
    GameEventListNotSet,
    PropTypeNotFound(String),
    GameEventUnknownId(String),
    UnknownPawnPrefix(String),
    UnknownEntityHandle(String),
    ClsIdOutOfBounds,
    UnknownGameEventVariant(String),
    FileNotFound(String),
    NoEvents,
    DecompressionFailure(String),
    NoSendTableMessage,
    UserIdNotFound,
    EventListFallbackNotFound(String),
    VoiceDataWriteError(String),
    UnknownDemoCmd(i32),
    IllegalPathOp,
    VectorResizeFailure,
    ImpossibleCmd,
    UnkVoiceFormat,
    MalformedVoicePacket,
}

impl std::error::Error for DemoParserError {}

impl fmt::Display for DemoParserError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{:?}", self)
    }
}

#[cfg(test)]
mod byte_skip_tests {
    use super::*;

    #[test]
    fn mixed_reads_match_bitwise_reference_across_refills() {
        let bytes: Vec<u8> = (0..4096).map(|n| ((n * 73 + n / 17) & 255) as u8).collect();
        let expected = |start: usize, count: usize| -> u32 {
            (0..count).fold(0, |value, offset| {
                value | (((bytes[(start + offset) / 8] >> ((start + offset) % 8)) & 1) as u32) << offset
            })
        };
        for prefix in 0..8 {
            let mut reader = Bitreader::new(&bytes);
            assert_eq!(reader.read_nbits(prefix as u32).unwrap(), expected(0, prefix));
            let mut position = prefix;
            for step in 0..160 {
                for width in [1, 0, 7, 32, 2, 17, 5] {
                    assert_eq!(reader.read_nbits(width).unwrap(), expected(position, width as usize));
                    position += width as usize;
                }
                let len = step % 9;
                if step % 3 == 0 {
                    reader.skip_n_bytes(len).unwrap();
                } else {
                    let reference: Vec<u8> = (0..len).map(|offset| expected(position + offset * 8, 8) as u8).collect();
                    if step % 3 == 1 {
                        assert_eq!(reader.read_n_bytes(len).unwrap(), reference);
                    } else {
                        let mut output = vec![0; len];
                        reader.read_n_bytes_mut(len, &mut output).unwrap();
                        assert_eq!(output, reference);
                    }
                }
                position += len * 8;
                assert_eq!(reader.bits_remaining(), Some(bytes.len() * 8 - position));
            }
        }
    }

    #[test]
    fn skipping_matches_reading_at_every_bit_alignment() {
        let bytes: Vec<u8> = (0..=255).collect();
        for prefix_bits in 0..16 {
            for len in [0, 1, 7, 8, 9, 31, 127, 250] {
                let mut skipped = Bitreader::new(&bytes);
                let mut copied = Bitreader::new(&bytes);
                assert_eq!(skipped.read_nbits(prefix_bits), copied.read_nbits(prefix_bits));
                skipped.skip_n_bytes(len).unwrap();
                copied.read_n_bytes(len).unwrap();
                assert_eq!(skipped.bits_remaining(), copied.bits_remaining());
                assert_eq!(skipped.read_nbits(17), copied.read_nbits(17));
                assert_eq!(skipped.read_n_bytes(1), copied.read_n_bytes(1));
            }
        }
    }

    #[test]
    fn consecutive_skips_preserve_exact_tail_and_end_of_stream() {
        let bytes = [0x8d, 0x35, 0x91, 0x77, 0xb4];
        let mut skipped = Bitreader::new(&bytes);
        let mut copied = Bitreader::new(&bytes);
        assert_eq!(skipped.read_nbits(3), copied.read_nbits(3));
        skipped.skip_n_bytes(1).unwrap();
        skipped.skip_n_bytes(3).unwrap();
        copied.read_n_bytes(4).unwrap();
        assert_eq!(skipped.read_nbits(5), copied.read_nbits(5));
        assert_eq!(skipped.bits_remaining(), Some(0));
        skipped.skip_n_bytes(0).unwrap();

        let mut aligned = Bitreader::new(&bytes);
        aligned.skip_n_bytes(bytes.len()).unwrap();
        assert_eq!(aligned.bits_remaining(), Some(0));
    }

    #[test]
    fn oversized_skip_is_rejected_without_advancing() {
        let bytes = [0xc5, 0xa4];
        for requested in [2, usize::MAX] {
            let mut skipped = Bitreader::new(&bytes);
            let mut original = Bitreader::new(&bytes);
            skipped.read_nbits(1).unwrap();
            original.read_nbits(1).unwrap();
            assert!(matches!(skipped.skip_n_bytes(requested), Err(DemoParserError::FailedByteRead(_))));
            assert_eq!(skipped.bits_remaining(), original.bits_remaining());
            assert_eq!(skipped.read_nbits(15), original.read_nbits(15));
        }
    }

    #[test]
    fn failed_byte_reads_leave_deferred_bits_readable() {
        for use_buffer in [false, true] {
            let bytes = [0xc5, 0xa4];
            let mut reader = Bitreader::new(&bytes);
            assert_eq!(reader.read_nbits(3).unwrap(), 5);
            let result = if use_buffer {
                reader.read_n_bytes_mut(2, &mut [0; 2])
            } else {
                reader.read_n_bytes(2).map(|_| ())
            };
            assert!(matches!(result, Err(DemoParserError::FailedByteRead(_))));
            assert_eq!(reader.bits_remaining(), Some(13));
            assert_eq!(reader.read_nbits(13).unwrap(), 0xa4c5 >> 3);
        }
    }
}
